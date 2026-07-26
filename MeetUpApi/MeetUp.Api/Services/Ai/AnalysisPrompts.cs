using System.Text;

namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Prompts are shared by every provider so that switching models changes quality, not shape.
/// Each prompt states the exact JSON contract, because the OpenAI-compatible providers only
/// guarantee "some JSON object" (json_object mode) rather than a strict schema.
/// </summary>
internal static class AnalysisPrompts
{
    public const string SystemPrompt =
        "You analyse meeting transcripts. Reply with a single JSON object and nothing else — " +
        "no prose, no markdown fences. Use only information present in the transcript; never invent " +
        "facts, names, or dates. If a field has no supporting content, return an empty string or empty array.";

    public const double ExtractionTemperature = 0.2;
    public const double DraftingTemperature = 0.5;

    public static string Summary(string transcript) =>
        $$"""
        Summarise the meeting transcript below.

        Return exactly this JSON shape:
        {"overview": "<2-4 sentence plain-text summary>", "keyTopics": ["<short topic>", "..."]}

        Return at most 8 key topics, each a few words.

        TRANSCRIPT:
        {{transcript}}
        """;

    public static string ActionItems(string transcript, IEnumerable<ParticipantInfo> participants) =>
        $$"""
        Extract concrete action items (tasks someone committed to do) from the transcript below.

        Return exactly this JSON shape:
        {"actionItems": [{"description": "<what must be done>", "assigneeName": "<participant name or null>", "dueDate": "<YYYY-MM-DD or null>"}]}

        Rules:
        - Only include tasks explicitly agreed to. Do not invent follow-ups.
        - assigneeName must match one of the participants listed below, or be null when unclear.
        - dueDate only when a specific date is stated or clearly implied. Otherwise null.
        - Return {"actionItems": []} when there are none.

        PARTICIPANTS:
        {{FormatParticipants(participants)}}

        TRANSCRIPT:
        {{transcript}}
        """;

    public static string Decisions(string transcript) =>
        $$"""
        Extract decisions that were actually settled in the transcript below.

        Return exactly this JSON shape:
        {"decisions": [{"description": "<the decision that was made>"}]}

        Only include decisions the group concluded — not open questions or suggestions.
        Return {"decisions": []} when there are none.

        TRANSCRIPT:
        {{transcript}}
        """;

    public static string FollowUpEmail(MeetingContext meeting) =>
        $$"""
        Draft a follow-up email that the host can send to everyone who attended.

        Return exactly this JSON shape:
        {"subject": "<concise subject line>", "bodyMarkdown": "<email body in markdown>"}

        The body should recap what was discussed, list any agreed action items with their owners,
        and stay professional and brief. Do not include a signature block.

        MEETING TITLE: {{meeting.Title}}
        DATE: {{(meeting.HeldUtc?.ToString("yyyy-MM-dd") ?? "unknown")}}

        PARTICIPANTS:
        {{FormatParticipants(meeting.Participants)}}

        TRANSCRIPT:
        {{meeting.Transcript}}
        """;

    private static string FormatParticipants(IEnumerable<ParticipantInfo> participants)
    {
        var builder = new StringBuilder();
        foreach (var participant in participants)
        {
            builder.AppendLine($"- {participant.DisplayName}");
        }

        return builder.Length == 0 ? "(none recorded)" : builder.ToString().TrimEnd();
    }
}
