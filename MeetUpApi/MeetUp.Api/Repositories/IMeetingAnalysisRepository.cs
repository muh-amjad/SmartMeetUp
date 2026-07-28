using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

/// <summary>
/// One repository for the whole analysis result set. Summary, action items, decisions and the
/// follow-up email are always written together by AiAnalysisJob and always belong to one meeting,
/// so keeping them behind a single seam avoids four near-identical repositories.
/// </summary>
public interface IMeetingAnalysisRepository
{
    Task<MeetingSummary?> GetSummaryAsync(Guid meetingId, CancellationToken ct);

    Task<IReadOnlyList<ActionItem>> GetActionItemsAsync(Guid meetingId, CancellationToken ct);

    /// <summary>
    /// Action items across every meeting the user hosted or attended — the cross-meeting view the
    /// dashboard and the action-items page are built on.
    /// </summary>
    Task<IReadOnlyList<ActionItem>> GetActionItemsForUserAsync(string userId, CancellationToken ct);

    Task<ActionItem?> GetActionItemAsync(Guid actionItemId, CancellationToken ct);

    Task<IReadOnlyList<Decision>> GetDecisionsAsync(Guid meetingId, CancellationToken ct);

    Task<FollowUpEmail?> GetFollowUpEmailAsync(Guid meetingId, CancellationToken ct);

    /// <summary>Removes any previous analysis for the meeting so a re-run replaces rather than duplicates.</summary>
    Task ClearAnalysisAsync(Guid meetingId, CancellationToken ct);

    Task AddAnalysisAsync(
        MeetingSummary summary,
        IEnumerable<ActionItem> actionItems,
        IEnumerable<Decision> decisions,
        FollowUpEmail followUpEmail,
        CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
