using Hangfire;
using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using MeetUp.Api.Services.AssemblyAi;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Runs once per recorded meeting: signs a download URL for the recording, submits it to
/// AssemblyAI, polls until done, then persists the diarized transcript.
/// No automatic Hangfire retries — a failed run marks the meeting Failed and leaves retrying
/// to the explicit POST /transcript/retry endpoint, so state transitions stay predictable.
/// </summary>
[AutomaticRetry(Attempts = 0)]
public sealed class TranscriptionJob : ITranscriptionJob
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private const int MaxPollAttempts = 20; // ~5 minutes

    private readonly IMeetingRepository _meetingRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly IBlobStorageService _blobStorage;
    private readonly IAssemblyAiClient _assemblyAiClient;
    private readonly ILogger<TranscriptionJob> _logger;

    public TranscriptionJob(
        IMeetingRepository meetingRepository,
        ITranscriptRepository transcriptRepository,
        IBlobStorageService blobStorage,
        IAssemblyAiClient assemblyAiClient,
        ILogger<TranscriptionJob> logger)
    {
        _meetingRepository = meetingRepository;
        _transcriptRepository = transcriptRepository;
        _blobStorage = blobStorage;
        _assemblyAiClient = assemblyAiClient;
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

        try
        {
            var audioUrl = await _blobStorage.GetSignedDownloadUrlAsync(meeting.RecordingBlobKey, TimeSpan.FromHours(4), ct);
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

            // No Phase 5 (AI analysis) or Phase 6 (speaker mapping) exist yet, so the pipeline
            // stops here — the meeting is as "done" as the current phases make it.
            meeting.Status = MeetingStatus.Ready;
            await _transcriptRepository.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting {MeetingId} transcribed: {UtteranceCount} utterances",
                meeting.Id, transcript.Utterances.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TranscriptionJob failed for meeting {MeetingId}", meetingId);
            meeting.Status = MeetingStatus.Failed;
            await _meetingRepository.SaveChangesAsync(ct);
            throw; // surfaces the failure in the Hangfire dashboard; no auto-retry (see attribute above)
        }
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
