namespace MeetUp.Api.Dtos.Meetings;

public sealed class SpeakingShareDto
{
    public string UserId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Seconds { get; set; }
    public double Percent { get; set; }
}

public sealed class MeetingAnalyticsDto
{
    public int TotalDurationSeconds { get; set; }
    public int ParticipantCount { get; set; }
    public int WordCount { get; set; }
    public double AverageWordsPerMinute { get; set; }
    public int TotalSpeakingSeconds { get; set; }
    public IReadOnlyList<SpeakingShareDto> SpeakingDistribution { get; set; } = Array.Empty<SpeakingShareDto>();
    public DateTime ComputedUtc { get; set; }
}

public sealed class WeeklyBucketDto
{
    /// <summary>Monday (UTC) of the week this bucket covers.</summary>
    public DateTime WeekStartUtc { get; set; }
    public int MeetingCount { get; set; }
    public int TotalMinutes { get; set; }
}

public sealed class AccountAnalyticsDto
{
    public double TotalMeetingHours { get; set; }
    public int MeetingCount { get; set; }
    public int AverageDurationMinutes { get; set; }
    public int TotalSpeakingSeconds { get; set; }
    public IReadOnlyList<string> MostDiscussedTopics { get; set; } = Array.Empty<string>();
    public IReadOnlyList<WeeklyBucketDto> WeeklyBreakdown { get; set; } = Array.Empty<WeeklyBucketDto>();
}
