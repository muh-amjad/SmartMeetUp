using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface IMeetingAnalyticsRepository
{
    Task AddAudioActivityAsync(IEnumerable<ParticipantAudioActivity> intervals, CancellationToken ct);

    Task<IReadOnlyList<ParticipantAudioActivity>> GetAudioActivityAsync(Guid meetingId, CancellationToken ct);

    Task<MeetingAnalytics?> GetAnalyticsAsync(Guid meetingId, CancellationToken ct);

    /// <summary>Inserts or replaces the analytics row so a recompute is idempotent.</summary>
    Task UpsertAnalyticsAsync(MeetingAnalytics analytics, CancellationToken ct);

    /// <summary>Analytics rows for every meeting the user took part in within the window.</summary>
    Task<IReadOnlyList<MeetingAnalytics>> GetAnalyticsForUserAsync(
        string userId, DateTime fromUtc, DateTime toUtc, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
