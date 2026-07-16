namespace MeetUp.Api.Dtos.Meetings;

/// <summary>Compact meeting for the history list view.</summary>
public sealed class MeetingListItemDto
{
    public Guid MeetingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;      // enum as string for readability
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ActualStartUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public int ParticipantCount { get; set; }
    public bool IsHost { get; set; }
}