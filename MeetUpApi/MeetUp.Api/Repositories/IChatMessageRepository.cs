using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface IChatMessageRepository
{
    Task AddAsync(ChatMessage message, CancellationToken ct);

    /// <summary>All chat messages for a meeting, ordered by time, with sender loaded.</summary>
    Task<IReadOnlyList<ChatMessage>> GetByMeetingAsync(Guid meetingId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}