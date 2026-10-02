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

    /// <summary>
    /// When the recording file actually began, as reported by Egress. Transcript timestamps are
    /// relative to this moment, which is usually several seconds after <see cref="ActualStartUtc"/>
    /// (the room exists before anyone joins, and the recorder needs time to start), so speaker
    /// matching uses it to line up client-reported speaking times with the transcript.
    /// </summary>
    public DateTime? RecordingStartedUtc { get; set; }

    // AI analysis: the provider is frozen at meeting-creation time so that changing your
    // preference later doesn't retroactively affect meetings already in flight.
    public string? AnalysisProviderRequested { get; set; }

    /// <summary>Provider that actually ran — differs from Requested when a fallback kicked in.</summary>
    public string? AnalysisProviderUsed { get; set; }

     public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<MeetingParticipant> Participants { get; set; } = new List<MeetingParticipant>();
    public ICollection<ChatMessage> ChatMessages { get; set; } = new List<ChatMessage>();
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