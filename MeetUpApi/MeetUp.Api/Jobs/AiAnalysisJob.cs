using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
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

        // Try the chosen provider, then every other configured one. Falling back only to the default
        // meant that when the default itself was the one failing, keys for the other providers sat
        // unused while every meeting ended up Failed.
        var candidates = new List<IAnalysisProvider> { host };
        candidates.AddRange(_providerFactory.GetFallbacks(host.Key));

        AnalysisBundle? bundle = null;
        IAnalysisProvider? providerUsed = null;

        foreach (var candidate in candidates)
        {
            try
            {
                bundle = await RunWithRetryAsync(candidate, context, ct);
                providerUsed = candidate;
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AiAnalysisJob: provider {ProviderKey} failed for meeting {MeetingId}",
                    candidate.Key, meetingId);
            }
        }

        if (bundle is null || providerUsed is null)
        {
            _logger.LogError(
                "AiAnalysisJob: every configured provider failed for meeting {MeetingId} (tried {Providers})",
                meetingId, string.Join(", ", candidates.Select(c => c.Key)));
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            return;
        }

        if (!string.Equals(providerUsed.Key, host.Key, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation(
                "Meeting {MeetingId} analysed by fallback provider {FallbackProvider} after {RequestedProvider} failed",
                meetingId, providerUsed.Key, host.Key);
        }

        await PersistAsync(meeting, bundle, providerUsed, participantEntities, ct);

        _logger.LogInformation(
            "Meeting {MeetingId} analysed with {ProviderKey}: {ActionItems} action item(s), {Decisions} decision(s)",
            meetingId, providerUsed.Key, bundle.ActionItems.Length, bundle.Decisions.Length);
    }

    private const int MaxAttemptsPerStep = 3;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    // "retry in 8.47s", "retry in 2m30s", "retry in 9h12m4.85s" — Gemini's per-day quota answers in
    // hours, and a seconds-only pattern missed those entirely.
    private static readonly Regex RetryIn = new(
        @"retry in (?:(?<h>\d+)h)?(?:(?<m>\d+)m)?(?:(?<s>\d+(?:\.\d+)?)s)?", RegexOptions.IgnoreCase);

    /// <summary>
    /// Runs the four analyses one after another, retrying each on its own when the provider is
    /// rate-limited or overloaded.
    ///
    /// They used to run all at once, and any failure re-sent all four immediately — up to eight
    /// requests within a few seconds. Free tiers allow only a handful a minute (Gemini's current
    /// flash model: five), so a single overloaded (503) response was enough to exhaust the quota
    /// and fail the whole analysis with 429s.
    /// </summary>
    private async Task<AnalysisBundle> RunWithRetryAsync(
        IAnalysisProvider provider, MeetingContext context, CancellationToken ct)
    {
        var summary = await WithRetryAsync(
            "summary", () => provider.GenerateSummaryAsync(context.Transcript, ct), ct);
        var actionItems = await WithRetryAsync(
            "action items", () => provider.ExtractActionItemsAsync(context.Transcript, context.Participants, ct), ct);
        var decisions = await WithRetryAsync(
            "decisions", () => provider.ExtractDecisionsAsync(context.Transcript, ct), ct);
        var email = await WithRetryAsync(
            "follow-up email", () => provider.DraftFollowUpEmailAsync(context, ct), ct);

        return new AnalysisBundle(summary, actionItems, decisions, email);
    }

    private async Task<T> WithRetryAsync<T>(string step, Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (HttpRequestException ex) when (attempt < MaxAttemptsPerStep && IsTransient(ex)
                                                  && RetryDelay(ex, attempt) is not null)
            {
                var delay = RetryDelay(ex, attempt)!.Value;
                _logger.LogInformation(
                    "AiAnalysisJob: {Step} got {StatusCode}; retrying in {DelaySeconds:0}s (attempt {Attempt} of {MaxAttempts})",
                    step, (int?)ex.StatusCode, delay.TotalSeconds, attempt + 1, MaxAttemptsPerStep);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>Rate limits, overload and network failures pass; bad keys and bad requests do not.</summary>
    private static bool IsTransient(HttpRequestException ex) =>
        ex.StatusCode is null
        || ex.StatusCode == HttpStatusCode.TooManyRequests
        || (int)ex.StatusCode >= 500;

    /// <summary>
    /// Waits as long as the provider asks — Gemini's 429 says "Please retry in 8.47s" — and otherwise
    /// backs off 5s, then 15s. Returns null when the provider asks for longer than is worth waiting
    /// (its daily quota says "retry in 9h…"): retrying could only fail again, so the job moves
    /// straight on to the next provider instead.
    /// </summary>
    internal static TimeSpan? RetryDelay(HttpRequestException ex, int attempt)
    {
        var requested = RequestedRetryDelay(ex.Message);
        if (requested is null)
        {
            return TimeSpan.FromSeconds(attempt == 1 ? 5 : 15);
        }

        return requested <= MaxRetryDelay ? requested : null;
    }

    private static TimeSpan? RequestedRetryDelay(string message)
    {
        var match = RetryIn.Match(message);
        if (!match.Success || match.Length == "retry in ".Length)
        {
            return null;
        }

        static double Part(Group group) =>
            group.Success ? double.Parse(group.Value, NumberStyles.Float, CultureInfo.InvariantCulture) : 0;

        var requested = TimeSpan.FromHours(Part(match.Groups["h"]))
            + TimeSpan.FromMinutes(Part(match.Groups["m"]))
            + TimeSpan.FromSeconds(Math.Ceiling(Part(match.Groups["s"])));

        // A second of margin so the retry lands after the window, not on its edge.
        return requested + TimeSpan.FromSeconds(1);
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
