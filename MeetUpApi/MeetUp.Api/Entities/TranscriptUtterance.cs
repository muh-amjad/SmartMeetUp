namespace MeetUp.Api.Entities;

public class TranscriptUtterance
{
    public Guid Id { get; set; }

    public Guid TranscriptId { get; set; }
    public Transcript? Transcript { get; set; }

    /// <summary>AssemblyAI's anonymous label — "A", "B", "C". Resolved to a real user in Phase 6.</summary>
    public string SpeakerLabel { get; set; } = string.Empty;

    public string? ParticipantUserId { get; set; }
    public ApplicationUser? Participant { get; set; }

    public int StartMs { get; set; }
    public int EndMs { get; set; }

    public string Text { get; set; } = string.Empty;

    public float Confidence { get; set; }
}
