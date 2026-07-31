namespace MeetUp.Api.Entities;

/// <summary>
/// One row per address a follow-up email actually went to. This is an audit trail, so the address
/// is stored as sent rather than looked up through the user — a later email change or a deleted
/// account must not rewrite who was contacted.
/// </summary>
public class FollowUpEmailRecipient
{
    public Guid Id { get; set; }

    public Guid FollowUpEmailId { get; set; }
    public FollowUpEmail? FollowUpEmail { get; set; }

    public string RecipientEmail { get; set; } = string.Empty;

    public string? RecipientUserId { get; set; }
    public ApplicationUser? RecipientUser { get; set; }

    public DateTime SentAtUtc { get; set; } = DateTime.UtcNow;
}
