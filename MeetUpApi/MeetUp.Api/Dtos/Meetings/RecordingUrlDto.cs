namespace MeetUp.Api.Dtos.Meetings;

public sealed class RecordingUrlDto
{
    public string Url { get; set; } = string.Empty;
    public DateTime ExpiresUtc { get; set; }
}
