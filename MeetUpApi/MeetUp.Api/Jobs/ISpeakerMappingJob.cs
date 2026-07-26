using Hangfire;

namespace MeetUp.Api.Jobs;

public interface ISpeakerMappingJob
{
    /// <summary>
    /// No automatic retries: analytics are supplementary, so a failure should surface in the
    /// dashboard without repeatedly rewriting transcript attributions. The attribute belongs on
    /// the interface because that is the type jobs are enqueued against.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid meetingId, CancellationToken ct);
}
