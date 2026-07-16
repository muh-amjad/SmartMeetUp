namespace MeetUp.Api.Dtos.Meetings;

public sealed class UpdateMeetingRequestDto
{
    /// <summary>New meeting title (optional). Max 200 chars.</summary>
    public string? Title { get; set; }

    /// <summary>Reschedule time (optional).</summary>
    public DateTime? ScheduledStartUtc { get; set; }
}