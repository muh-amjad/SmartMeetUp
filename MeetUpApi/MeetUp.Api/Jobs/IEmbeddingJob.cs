using Hangfire;

namespace MeetUp.Api.Jobs;

public interface IEmbeddingJob
{
    /// <summary>
    /// No automatic retries: search is supplementary and a failure must not repeatedly rewrite a
    /// meeting's chunks. The attribute sits on the interface because that is the type jobs are
    /// enqueued against.
    /// </summary>
    [AutomaticRetry(Attempts = 0)]
    Task RunAsync(Guid meetingId, CancellationToken ct);
}
