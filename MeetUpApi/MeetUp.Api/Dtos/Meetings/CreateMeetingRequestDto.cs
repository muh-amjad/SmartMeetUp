namespace MeetUp.Api.Dtos.Meetings;

public sealed class CreateMeetingRequestDto
{
    /// <summary>Optional meeting title. Defaults to "Untitled Meeting".</summary>
    public string? Title { get; set; }
}