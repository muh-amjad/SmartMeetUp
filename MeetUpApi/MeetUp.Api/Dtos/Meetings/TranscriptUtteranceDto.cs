namespace MeetUp.Api.Dtos.Meetings;

public sealed class TranscriptUtteranceDto
{
    public string SpeakerLabel { get; set; } = string.Empty;
    public string? ParticipantUserId { get; set; }
    public string? ParticipantUsername { get; set; }
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public float Confidence { get; set; }
}
