namespace MeetUp.Api.Services.Ai;

public interface IAnalysisProvider
{
    /// <summary>Config key, e.g. "gemini-2.5-flash".</summary>
    string Key { get; }

    string DisplayName { get; }

    string ModelId { get; }

    Task<SummaryResult> GenerateSummaryAsync(string transcript, CancellationToken ct);

    Task<ActionItemResult[]> ExtractActionItemsAsync(
        string transcript, IEnumerable<ParticipantInfo> participants, CancellationToken ct);

    Task<DecisionResult[]> ExtractDecisionsAsync(string transcript, CancellationToken ct);

    Task<EmailDraftResult> DraftFollowUpEmailAsync(MeetingContext meeting, CancellationToken ct);
}
