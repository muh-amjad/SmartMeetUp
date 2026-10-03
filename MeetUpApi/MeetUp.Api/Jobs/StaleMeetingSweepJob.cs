using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Fails meetings that have been Processing for far longer than processing can take.
///
/// The meeting list will not open a Processing meeting, and transcription can only be retried once
/// a meeting has Failed — so a meeting that never leaves Processing could neither be viewed nor
/// recovered. That happens whenever a step never reports back: a lost LiveKit webhook, or the API
/// stopping mid-job. The longest legitimate run (transcription polls for up to ~25 minutes, then
/// analysis) is well inside the cut-off.
/// </summary>
public sealed class StaleMeetingSweepJob : IStaleMeetingSweepJob
{
    /// <summary>How long a meeting may stay Processing after it ended before it is given up on.</summary>
    public static readonly TimeSpan MaxProcessingTime = TimeSpan.FromHours(2);

    private readonly AppDbContext _dbContext;
    private readonly ILogger<StaleMeetingSweepJob> _logger;

    public StaleMeetingSweepJob(AppDbContext dbContext, ILogger<StaleMeetingSweepJob> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - MaxProcessingTime;

        var stuck = await _dbContext.Meetings
            .Where(m => m.Status == MeetingStatus.Processing && (m.EndedUtc ?? m.UpdatedUtc) < cutoff)
            .ToListAsync(ct);

        if (stuck.Count == 0)
        {
            return;
        }

        foreach (var meeting in stuck)
        {
            meeting.Status = MeetingStatus.Failed;
            meeting.UpdatedUtc = DateTime.UtcNow;

            _logger.LogWarning(
                "Meeting {MeetingId} was still Processing {Hours:0.#}h after it ended; marked Failed so it " +
                "can be opened and retried",
                meeting.Id, (DateTime.UtcNow - (meeting.EndedUtc ?? meeting.UpdatedUtc)).TotalHours);
        }

        await _dbContext.SaveChangesAsync(ct);
    }
}
