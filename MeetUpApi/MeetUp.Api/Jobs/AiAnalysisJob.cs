using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services.Ai;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Turns a finished transcript into a summary, action items, decisions and a follow-up email draft
/// using whichever LLM provider the fallback chain resolves to.
/// </summary>
public sealed class AiAnalysisJob : IAiAnalysisJob
{
    private readonly IMeetingRepository _meetingRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly IMeetingParticipantRepository _participantRepository;
    private readonly IMeetingAnalysisRepository _analysisRepository;
    private readonly IAnalysisProviderFactory _providerFactory;
    private readonly ILogger<AiAnalysisJob> _logger;

    public AiAnalysisJob(
        IMeetingRepository meetingRepository,
        ITranscriptRepository transcriptRepository,
        IMeetingParticipantRepository participantRepository,
        IMeetingAnalysisRepository analysisRepository,
        IAnalysisProviderFactory providerFactory,
        ILogger<AiAnalysisJob> logger)
    {
        _meetingRepository = meetingRepository;
        _transcriptRepository = transcriptRepository;
        _participantRepository = participantRepository;
        _analysisRepository = analysisRepository;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task RunAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct);
        if (meeting is null)
        {
            _logger.LogWarning("AiAnalysisJob: meeting {MeetingId} not found", meetingId);
            return;
        }

        var transcript = await _transcriptRepository.GetByMeetingIdAsync(meetingId, ct);
        if (transcript is null || string.IsNullOrWhiteSpace(transcript.FullText))
        {
            _logger.LogWarning("AiAnalysisJob: meeting {MeetingId} has no transcript to analyse", meetingId);
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            return;
        }

        var participantEntities = await _participantRepository.GetByMeetingAsync(meetingId, ct);
        var participants = participantEntities
            .Select(p => new ParticipantInfo(
                p.UserId,
                p.User?.DisplayName is { Length: > 0 } name ? name : p.User?.UserName ?? "Unknown"))
            .ToList();

        var host = _providerFactory.Resolve(meeting.AnalysisProviderRequested, meeting.Host?.PreferredAnalysisProviderKey);
        if (host is null)
        {
            _logger.LogWarning(
                "AiAnalysisJob: no AI provider is configured (no API keys set) — skipping analysis for meeting {MeetingId}",
                meetingId);
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            return;
        }

        var context = new MeetingContext
        {
            Title = meeting.Title,
            HeldUtc = meeting.ActualStartUtc ?? meeting.CreatedUtc,
            Transcript = transcript.FullText,
            Participants = participants,
        };

        AnalysisBundle bundle;
        IAnalysisProvider providerUsed = host;

        try
        {
            bundle = await RunWithRetryAsync(host, context, ct);
        }
        catch (Exception ex)
        {
            var fallback = _providerFactory.GetDefault();

            if (fallback is null || string.Equals(fallback.Key, host.Key, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogError(ex, "AiAnalysisJob: provider {ProviderKey} failed and no other provider is available", host.Key);
                meeting.Status = MeetingStatus.Failed;
                await _meetingRepository.SaveChangesAsync(ct);
                return;
            }

            _logger.LogWarning(ex,
                "AiAnalysisJob: provider {FailedProvider} failed for meeting {MeetingId}; falling back to {FallbackProvider}",
                host.Key, meetingId, fallback.Key);

            try
            {
                bundle = await RunWithRetryAsync(fallback, context, ct);
                providerUsed = fallback;
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx,
                    "AiAnalysisJob: fallback provider {FallbackProvider} also failed for meeting {MeetingId}",
                    fallback.Key, meetingId);
                meeting.Status = MeetingStatus.Failed;
                await _meetingRepository.SaveChangesAsync(ct);
                return;
            }
        }

        await PersistAsync(meeting, bundle, providerUsed, participantEntities, ct);

        _logger.LogInformation(
            "Meeting {MeetingId} analysed with {ProviderKey}: {ActionItems} action item(s), {Decisions} decision(s)",
            meetingId, providerUsed.Key, bundle.ActionItems.Length, bundle.Decisions.Length);
    }

    /// <summary>Runs the four analyses concurrently, retrying the whole set once on the same provider.</summary>
    private static async Task<AnalysisBundle> RunWithRetryAsync(
        IAnalysisProvider provider, MeetingContext context, CancellationToken ct)
    {
        try
        {
            return await RunAllAsync(provider, context, ct);
        }
        catch
        {
            return await RunAllAsync(provider, context, ct);
        }
    }

    private static async Task<AnalysisBundle> RunAllAsync(
        IAnalysisProvider provider, MeetingContext context, CancellationToken ct)
    {
        var summaryTask = provider.GenerateSummaryAsync(context.Transcript, ct);
        var actionItemsTask = provider.ExtractActionItemsAsync(context.Transcript, context.Participants, ct);
        var decisionsTask = provider.ExtractDecisionsAsync(context.Transcript, ct);
        var emailTask = provider.DraftFollowUpEmailAsync(context, ct);

        await Task.WhenAll(summaryTask, actionItemsTask, decisionsTask, emailTask);

        return new AnalysisBundle(
            await summaryTask,
            await actionItemsTask,
            await decisionsTask,
            await emailTask);
    }

    private async Task PersistAsync(
        Meeting meeting,
        AnalysisBundle bundle,
        IAnalysisProvider provider,
        IReadOnlyList<MeetingParticipant> participants,
        CancellationToken ct)
    {
        // A retry re-analyses from scratch, so drop whatever a previous run produced.
        await _analysisRepository.ClearAnalysisAsync(meeting.Id, ct);

        var summary = new MeetingSummary
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            OverviewText = bundle.Summary.Overview,
            KeyTopics = bundle.Summary.KeyTopics,
            ProviderKey = provider.Key,
            ModelUsed = provider.ModelId,
            GeneratedUtc = DateTime.UtcNow,
        };

        var actionItems = bundle.ActionItems
            .Select(a => new ActionItem
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                Description = Clamp(a.Description, 1000),
                AssigneeNameRaw = a.AssigneeNameRaw,
                AssigneeUserId = MatchParticipant(a.AssigneeNameRaw, participants),
                DueDateUtc = a.DueDateUtc,
                Status = ActionItemStatus.Open,
                CreatedUtc = DateTime.UtcNow,
            })
            .ToList();

        var decisions = bundle.Decisions
            .Select(d => new Decision
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                Description = Clamp(d.Description, 1000),
                CreatedUtc = DateTime.UtcNow,
            })
            .ToList();

        var email = new FollowUpEmail
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            Subject = Clamp(bundle.Email.Subject, 300),
            BodyMarkdown = bundle.Email.BodyMarkdown,
            Status = FollowUpEmailStatus.Draft,
            CreatedUtc = DateTime.UtcNow,
        };

        await _analysisRepository.AddAnalysisAsync(summary, actionItems, decisions, email, ct);

        meeting.AnalysisProviderUsed = provider.Key;
        meeting.Status = MeetingStatus.Ready;
        meeting.UpdatedUtc = DateTime.UtcNow;

        await _analysisRepository.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Maps the name the model wrote back to a real participant. Deliberately conservative: an
    /// unmatched name stays in AssigneeNameRaw rather than being guessed onto the wrong person.
    /// </summary>
    private static string? MatchParticipant(string? rawName, IReadOnlyList<MeetingParticipant> participants)
    {
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return null;
        }

        var name = rawName.Trim();

        var exact = participants.FirstOrDefault(p =>
            string.Equals(p.User?.DisplayName, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.User?.UserName, name, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return exact.UserId;
        }

        // "Ali" should still match the participant "Ali Raza", but only when exactly one fits.
        var partial = participants
            .Where(p =>
                (p.User?.DisplayName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.User?.UserName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        return partial.Count == 1 ? partial[0].UserId : null;
    }

    private static string Clamp(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record AnalysisBundle(
        SummaryResult Summary,
        ActionItemResult[] ActionItems,
        DecisionResult[] Decisions,
        EmailDraftResult Email);
}
