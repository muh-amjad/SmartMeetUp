namespace MeetUp.Api.Entities;

public class ChatMessage
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string SenderUserId { get; set; } = string.Empty;
    public ApplicationUser? Sender { get; set; }

    /// <summary>Max 2000 chars — enforced at DB level and in FluentValidation.</summary>
    public string Text { get; set; } = string.Empty;

    public DateTime SentUtc { get; set; } = DateTime.UtcNow;
}