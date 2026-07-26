using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface ITranscriptRepository
{
    /// <summary>Transcript with utterances loaded, ordered by start time. Null if not transcribed yet.</summary>
    Task<Transcript?> GetByMeetingIdAsync(Guid meetingId, CancellationToken ct);

    Task AddAsync(Transcript transcript, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
