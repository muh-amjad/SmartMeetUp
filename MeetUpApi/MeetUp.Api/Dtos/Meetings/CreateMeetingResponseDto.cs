namespace MeetUp.Api.Dtos.Meetings;

public sealed class CreateMeetingResponseDto
{
    public Guid MeetingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string LivekitToken { get; set; } = string.Empty;
    public string LivekitWsUrl { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
}