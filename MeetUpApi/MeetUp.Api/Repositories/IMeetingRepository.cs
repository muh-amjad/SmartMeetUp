using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface IMeetingRepository
{
    Task AddAsync(Meeting meeting, CancellationToken ct);
    Task<Meeting?> GetByIdAsync(Guid id, CancellationToken ct);
    Task UpdateAsync(Meeting meeting, CancellationToken ct);
    Task DeleteAsync(Meeting meeting, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
    /// <summary>
    /// Paginated list of meetings the caller was involved in (as host or participant).
    /// </summary>
    Task<IReadOnlyList<Meeting>> GetUserMeetingsAsync(
        string userId,
        MeetingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct);   // ← naya
}