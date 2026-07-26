namespace MeetUp.Api.Services.Ai;

public sealed record ParticipantInfo(string UserId, string DisplayName);

public sealed class MeetingContext
{
    public string Title { get; set; } = string.Empty;
    public DateTime? HeldUtc { get; set; }
    public string Transcript { get; set; } = string.Empty;
    public IReadOnlyList<ParticipantInfo> Participants { get; set; } = Array.Empty<ParticipantInfo>();
}

public sealed class SummaryResult
{
    public string Overview { get; set; } = string.Empty;
    public List<string> KeyTopics { get; set; } = new();
}

public sealed class ActionItemResult
{
    public string Description { get; set; } = string.Empty;

    /// <summary>Name exactly as the model reported it; matched to a user id by the job.</summary>
    public string? AssigneeNameRaw { get; set; }

    public DateTime? DueDateUtc { get; set; }
}

public sealed class DecisionResult
{
    public string Description { get; set; } = string.Empty;
}

public sealed class EmailDraftResult
{
    public string Subject { get; set; } = string.Empty;
    public string BodyMarkdown { get; set; } = string.Empty;
}
