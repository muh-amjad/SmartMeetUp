namespace MeetUp.Api.Entities;

public class ActionItem
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? AssigneeUserId { get; set; }
    public ApplicationUser? Assignee { get; set; }

    /// <summary>Raw name as the LLM extracted it, kept even when it can't be matched to a user.</summary>
    public string? AssigneeNameRaw { get; set; }

    public DateTime? DueDateUtc { get; set; }

    public ActionItemStatus Status { get; set; } = ActionItemStatus.Open;
    public DateTime? CompletedUtc { get; set; }

    public Guid? SourceUtteranceId { get; set; }
    public TranscriptUtterance? SourceUtterance { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum ActionItemStatus
{
    Open = 0,
    Done = 1,
    Cancelled = 2,
}
