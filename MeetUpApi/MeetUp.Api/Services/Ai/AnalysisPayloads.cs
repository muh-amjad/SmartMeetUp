using System.Text.Json.Serialization;

namespace MeetUp.Api.Services.Ai;

/// <summary>Raw JSON shapes the models are asked to return, before mapping to the public result types.</summary>
internal sealed class SummaryPayload
{
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("keyTopics")]
    public List<string>? KeyTopics { get; set; }
}

internal sealed class ActionItemsPayload
{
    [JsonPropertyName("actionItems")]
    public List<ActionItemPayload>? ActionItems { get; set; }
}

internal sealed class ActionItemPayload
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("assigneeName")]
    public string? AssigneeName { get; set; }

    [JsonPropertyName("dueDate")]
    public string? DueDate { get; set; }
}

internal sealed class DecisionsPayload
{
    [JsonPropertyName("decisions")]
    public List<DecisionPayload>? Decisions { get; set; }
}

internal sealed class DecisionPayload
{
    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal sealed class EmailPayload
{
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("bodyMarkdown")]
    public string? BodyMarkdown { get; set; }
}
