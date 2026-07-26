namespace MeetUp.Api.Dtos.Meetings;

public sealed class MeetingSummaryDto
{
    public string OverviewText { get; set; } = string.Empty;
    public IReadOnlyList<string> KeyTopics { get; set; } = Array.Empty<string>();
    public string ProviderKey { get; set; } = string.Empty;
    public string ProviderDisplayName { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = string.Empty;
    public DateTime GeneratedUtc { get; set; }
}

public sealed class ActionItemDto
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? AssigneeNameRaw { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? CompletedUtc { get; set; }
    public int? SourceStartMs { get; set; }
}

public sealed class UpdateActionItemRequestDto
{
    public string? Description { get; set; }
    public string? Status { get; set; }
    public string? AssigneeUserId { get; set; }
    public DateTime? DueDateUtc { get; set; }

    /// <summary>Set true to clear the due date, since a null DueDateUtc means "leave unchanged".</summary>
    public bool ClearDueDate { get; set; }
}

public sealed class DecisionDto
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public int? SourceStartMs { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class FollowUpEmailDto
{
    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? SentUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public sealed class UpdateFollowUpEmailRequestDto
{
    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
}

public sealed class AnalysisProviderDto
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsFree { get; set; }
    public bool IsDefault { get; set; }
    public int ContextWindow { get; set; }
}

public sealed class UserPreferencesDto
{
    public string? PreferredAnalysisProviderKey { get; set; }
}
