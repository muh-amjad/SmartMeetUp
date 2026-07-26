using Hangfire;

namespace MeetUp.Api.Jobs;

public interface ITranscriptionJob
{
    /// <summary>
    /// No automatic retries: a failed run marks the meeting Failed and retrying is left to the
    /// explicit POST /transcript/retry endpoint, so state transitions stay predictable.
    /// The attribute lives here rather than on the implementation because jobs are enqueued
    /// against this interface, and that is where Hangfire looks for it.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid meetingId, CancellationToken ct);
}
