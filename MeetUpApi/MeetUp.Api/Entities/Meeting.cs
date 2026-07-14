namespace MeetUp.Api.Entities;

public class Meeting
{
    public Guid Id { get; set; }

    // Who created the meeting
    public string HostUserId { get; set; } = string.Empty;
    public ApplicationUser? Host { get; set; }

    // Meeting metadata
    public string Title { get; set; } = "Untitled Meeting";
    public DateTime? ScheduledStartUtc { get; set; }
    public DateTime? ActualStartUtc { get; set; }
    public DateTime? EndedUtc { get; set; }
    public MeetingStatus Status { get; set; } = MeetingStatus.Scheduled;

    // LiveKit integration — unique room name per meeting
    public string LiveKitRoomName { get; set; } = string.Empty;

    // Recording pipeline (populated by Phase 3 webhook events but columns exist from now)
    public string? EgressId { get; set; }
    public string? RecordingBlobKey { get; set; }
    public int? RecordingDurationSeconds { get; set; }

    // Audit
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum MeetingStatus
{
    Scheduled = 0,
    Live = 1,
    Ended = 2,
    Processing = 3,
    Ready = 4,
    Failed = 5
}