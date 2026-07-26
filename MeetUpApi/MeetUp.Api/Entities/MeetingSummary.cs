namespace MeetUp.Api.Entities;

public class MeetingSummary
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string OverviewText { get; set; } = string.Empty;

    /// <summary>Stored as jsonb.</summary>
    public List<string> KeyTopics { get; set; } = new();

    /// <summary>Which provider produced this, e.g. "gemini-2.5-flash".</summary>
    public string ProviderKey { get; set; } = string.Empty;

    /// <summary>Exact model id reported by the provider.</summary>
    public string ModelUsed { get; set; } = string.Empty;

    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
}
