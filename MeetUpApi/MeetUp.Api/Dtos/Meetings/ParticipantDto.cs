namespace MeetUp.Api.Dtos.Meetings;

public sealed class ParticipantDto
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime? JoinedUtc { get; set; }
    public DateTime? LeftUtc { get; set; }
    public int SpeakingSeconds { get; set; }
}