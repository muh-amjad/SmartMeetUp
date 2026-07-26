using Hangfire;

namespace MeetUp.Api.Jobs;

public interface IAiAnalysisJob
{
    /// <summary>
    /// No automatic retries — the job already falls back to the default provider internally, and a
    /// genuine failure marks the meeting Failed for the explicit /analysis/retry endpoint to pick up.
    /// The attribute must sit on the interface: that is the type jobs are enqueued against, and it
    /// is where Hangfire looks for it.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid meetingId, CancellationToken ct);
}
