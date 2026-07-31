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

/// <summary>Who a follow-up email would go to, so the host can confirm before sending.</summary>
public sealed class FollowUpRecipientsDto
{
    public IReadOnlyList<FollowUpRecipientDto> Recipients { get; set; } = Array.Empty<FollowUpRecipientDto>();

    /// <summary>Participants excluded because they opted out of follow-up emails.</summary>
    public int OptedOutCount { get; set; }

    /// <summary>False when the server has no email transport configured; sending will refuse.</summary>
    public bool CanSend { get; set; }
}

public sealed class FollowUpRecipientDto
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
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

    /// <summary>Null on a PATCH means "leave unchanged", so the flag is nullable on the wire.</summary>
    public bool? OptOutFollowUpEmails { get; set; }
}

/// <summary>An action item plus the meeting it came from, for the cross-meeting list.</summary>
public sealed class ActionItemWithMeetingDto
{
    public Guid Id { get; set; }
    public Guid MeetingId { get; set; }
    public string MeetingTitle { get; set; } = string.Empty;
    public DateTime MeetingDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? AssigneeUserId { get; set; }
    public string? AssigneeUsername { get; set; }
    public string? AssigneeNameRaw { get; set; }
    public DateTime? DueDateUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsOverdue { get; set; }
    public bool IsAssignedToMe { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

public sealed class UpdateProfileRequestDto
{
    public string DisplayName { get; set; } = string.Empty;
}

public sealed class ChangePasswordRequestDto
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

public sealed class ProfileDto
{
    public string UserId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
