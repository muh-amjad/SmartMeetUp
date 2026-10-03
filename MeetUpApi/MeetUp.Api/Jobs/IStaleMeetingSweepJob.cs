using Hangfire;

namespace MeetUp.Api.Jobs;

public interface IStaleMeetingSweepJob
{
    /// <summary>
    /// No automatic retries: it runs again on its own schedule, so a failed run costs nothing.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    Task RunAsync(CancellationToken ct);
}
