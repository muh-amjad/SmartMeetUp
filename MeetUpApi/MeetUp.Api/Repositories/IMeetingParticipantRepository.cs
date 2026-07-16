using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface IMeetingParticipantRepository
{
    Task AddAsync(MeetingParticipant participant, CancellationToken ct);

    /// <summary>Find an existing participant row, if any (used to update LeftUtc etc.).</summary>
    Task<MeetingParticipant?> GetByMeetingAndUserAsync(Guid meetingId, string userId, CancellationToken ct);

    /// <summary>All participants of a meeting, including user navigation.</summary>
    Task<IReadOnlyList<MeetingParticipant>> GetByMeetingAsync(Guid meetingId, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}