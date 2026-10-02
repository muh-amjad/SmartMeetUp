using Hangfire;
using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using MeetUp.Api.Services.Ai;
using MeetUp.Api.Options;
using MeetUp.Api.Services.AssemblyAi;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Runs once per recorded meeting: signs a download URL for the recording, submits it to
/// AssemblyAI, polls until done, then persists the diarized transcript.
/// Retry policy is declared on ITranscriptionJob (that is the type jobs are enqueued against).
/// </summary>
public sealed class TranscriptionJob : ITranscriptionJob
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    // ~25 minutes. Five minutes was not enough: AssemblyAI with speaker labels and chapters takes
    // longer than that on a half-hour meeting, so longer meetings timed out and showed as Failed.
    // Kept under Hangfire's 30-minute invisibility timeout, past which a still-running job is
    // assumed dead and handed to another worker, submitting the same recording twice.
    private const int MaxPollAttempts = 100;

    private readonly IMeetingRepository _meetingRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly IBlobStorageService _blobStorage;
    private readonly IAssemblyAiClient _assemblyAiClient;
    private readonly IAnalysisProviderFactory _analysisProviderFactory;
    private readonly AssemblyAiOptions _assemblyAiOptions;
    private readonly ILogger<TranscriptionJob> _logger;

    public TranscriptionJob(
        IMeetingRepository meetingRepository,
        ITranscriptRepository transcriptRepository,
        IBlobStorageService blobStorage,
        IAssemblyAiClient assemblyAiClient,
        IAnalysisProviderFactory analysisProviderFactory,
        IOptions<AssemblyAiOptions> assemblyAiOptions,
        ILogger<TranscriptionJob> logger)
    {
        _meetingRepository = meetingRepository;
        _transcriptRepository = transcriptRepository;
        _blobStorage = blobStorage;
        _assemblyAiClient = assemblyAiClient;
        _analysisProviderFactory = analysisProviderFactory;
        _assemblyAiOptions = assemblyAiOptions.Value;
        _logger = logger;
    }

    public async Task RunAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct);
        if (meeting is null)
        {
            _logger.LogWarning("TranscriptionJob: meeting {MeetingId} not found", meetingId);
            return;
        }

        if (string.IsNullOrWhiteSpace(meeting.RecordingBlobKey))
        {
            _logger.LogWarning("TranscriptionJob: meeting {MeetingId} has no recording to transcribe", meetingId);
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            return;
        }

        // Transcription is optional (see deploy/README.md: "meetings record but are never
        // transcribed"). Calling AssemblyAI without a key only produces a 401, which used to mark a
        // perfectly good recording Failed and show it as a failed analysis. The recording is the
        // finished product here, so the meeting is Ready.
        if (string.IsNullOrWhiteSpace(_assemblyAiOptions.ApiKey))
        {
            _logger.LogWarning(
                "TranscriptionJob: no AssemblyAI API key is configured, so meeting {MeetingId} is kept " +
                "as a recording only. Set AssemblyAi:ApiKey to enable transcripts and analysis.",
                meetingId);
            meeting.Status = MeetingStatus.Ready;
            await _meetingRepository.SaveChangesAsync(ct);
            return;
        }

        try
        {
            var audioUrl = await GetAudioUrlForAssemblyAiAsync(meeting.RecordingBlobKey, ct);
            var transcriptId = await _assemblyAiClient.SubmitTranscriptionAsync(audioUrl, ct);
            var result = await PollUntilDoneAsync(transcriptId, ct);

            if (result.Status != "completed")
            {
                _logger.LogWarning(
                    "TranscriptionJob: AssemblyAI transcript {TranscriptId} ended with status {Status}: {Error}",
                    transcriptId, result.Status, result.Error);
                meeting.Status = MeetingStatus.Failed;
                await _meetingRepository.SaveChangesAsync(ct);
                return;
            }

            var transcript = new Transcript
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                Language = result.LanguageCode ?? "en",
                FullText = result.Text ?? string.Empty,
                AssemblyAiTranscriptId = transcriptId,
                CreatedUtc = DateTime.UtcNow,
            };

            foreach (var utterance in result.Utterances ?? new List<AssemblyAiUtterance>())
            {
                transcript.Utterances.Add(new TranscriptUtterance
                {
                    Id = Guid.NewGuid(),
                    TranscriptId = transcript.Id,
                    SpeakerLabel = utterance.Speaker,
                    StartMs = utterance.Start,
                    EndMs = utterance.End,
                    Text = utterance.Text,
                    Confidence = (float)utterance.Confidence,
                });
            }

            await _transcriptRepository.AddAsync(transcript, ct);

            // Hand off to AI analysis, which owns the transition to Ready. When no provider has an
            // API key the meeting is already as finished as it can get, so complete it here instead
            // of queueing work that could only fail.
            var analysisAvailable = _analysisProviderFactory.GetDefault() is not null;
            meeting.Status = analysisAvailable ? MeetingStatus.Processing : MeetingStatus.Ready;

            await _transcriptRepository.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting {MeetingId} transcribed: {UtteranceCount} utterances",
                meeting.Id, transcript.Utterances.Count);

            // Speaker attribution, analytics and search indexing only need the transcript, so they
            // run regardless of whether AI analysis is configured, and independently of it — none of
            // these jobs touch each other's data or the meeting status.
            BackgroundJob.Enqueue<ISpeakerMappingJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));
            BackgroundJob.Enqueue<IEmbeddingJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));

            if (analysisAvailable)
            {
                BackgroundJob.Enqueue<IAiAnalysisJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));
            }
            else
            {
                _logger.LogInformation(
                    "Meeting {MeetingId}: skipping AI analysis, no provider has an API key configured", meeting.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TranscriptionJob failed for meeting {MeetingId}", meetingId);
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            throw; // surfaces the failure in the Hangfire dashboard; no auto-retry (see attribute above)
        }
    }

    /// <summary>
    /// A URL AssemblyAI can fetch the recording from: either a presigned link straight into storage,
    /// or — when storage is not reachable from the internet — the URL of a copy uploaded from here.
    /// </summary>
    private async Task<string> GetAudioUrlForAssemblyAiAsync(string blobKey, CancellationToken ct)
    {
        if (!_assemblyAiOptions.UploadRecordings)
        {
            return await _blobStorage.GetSignedDownloadUrlAsync(blobKey, TimeSpan.FromHours(4), ct);
        }

        await using var media = await _blobStorage.OpenReadAsync(blobKey, ct);
        return await _assemblyAiClient.UploadAsync(media, ct);
    }

    private async Task<AssemblyAiTranscriptResult> PollUntilDoneAsync(string transcriptId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            var result = await _assemblyAiClient.GetTranscriptAsync(transcriptId, ct);
            if (result.Status is "completed" or "error")
            {
                return result;
            }

            await Task.Delay(PollInterval, ct);
        }

        return new AssemblyAiTranscriptResult
        {
            Id = transcriptId,
            Status = "error",
            Error = $"Timed out after {MaxPollAttempts} polling attempts",
        };
    }
}
