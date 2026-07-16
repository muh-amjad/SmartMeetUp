namespace MeetUp.Api.Dtos.Meetings;

/// <summary>Full meeting detail with participants — for /api/meetings/{id}.</summary>
public sealed class MeetingDetailDto
{
    public Guid MeetingId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string HostUserId { get; set; } = string.Empty;
    public string HostUsername { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ActualStartUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public string LiveKitRoomName { get; set; } = string.Empty;
    public int? RecordingDurationSeconds { get; set; }
    public DateTime CreatedUtc { get; set; }
    public bool IsHost { get; set; }
    public List<ParticipantDto> Participants { get; set; } = new();
}