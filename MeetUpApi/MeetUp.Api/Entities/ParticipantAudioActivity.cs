namespace MeetUp.Api.Entities;

/// <summary>
/// One interval during which a participant was an active speaker, in milliseconds from the
/// meeting's actual start. Used to attach the transcript's anonymous speaker labels to real users.
/// </summary>
public class ParticipantAudioActivity
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }

    public int StartedSpeakingMs { get; set; }
    public int StoppedSpeakingMs { get; set; }
}
