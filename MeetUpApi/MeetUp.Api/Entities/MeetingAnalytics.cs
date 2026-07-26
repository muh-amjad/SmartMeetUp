namespace MeetUp.Api.Entities;

public class MeetingAnalytics
{
    /// <summary>Primary key and FK — one analytics row per meeting.</summary>
    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public int TotalDurationSeconds { get; set; }
    public int ParticipantCount { get; set; }

    /// <summary>Stored as jsonb.</summary>
    public List<SpeakingShare> SpeakingDistribution { get; set; } = new();

    public int WordCount { get; set; }
    public double AverageWordsPerMinute { get; set; }

    public DateTime ComputedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SpeakingShare
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Seconds { get; set; }
    public double Percent { get; set; }
}
