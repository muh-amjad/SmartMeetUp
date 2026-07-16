namespace MeetUp.Api.Entities;

public class MeetingParticipant
{
    public Guid Id { get; set; }

    // FK to the meeting they joined
    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    // FK to the user
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public ParticipantRole Role { get; set; } = ParticipantRole.Participant;

    // Set by LiveKit participant_joined webhook
    public DateTime? JoinedUtc { get; set; }

    // Set by LiveKit participant_left webhook
    public DateTime? LeftUtc { get; set; }

    // Computed in Phase 6 from active_speaker_changed events
    public int SpeakingSeconds { get; set; }
}

public enum ParticipantRole
{
    Host = 0,
    Participant = 1,
}