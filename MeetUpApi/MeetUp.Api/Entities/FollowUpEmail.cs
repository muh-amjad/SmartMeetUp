namespace MeetUp.Api.Entities;

public class FollowUpEmail
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;

    public FollowUpEmailStatus Status { get; set; } = FollowUpEmailStatus.Draft;

    public string? EditedByUserId { get; set; }
    public ApplicationUser? EditedBy { get; set; }

    /// <summary>Set in Phase 9 when sending is implemented.</summary>
    public DateTime? SentUtc { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum FollowUpEmailStatus
{
    Draft = 0,
    Sent = 1,
    Discarded = 2,
}
