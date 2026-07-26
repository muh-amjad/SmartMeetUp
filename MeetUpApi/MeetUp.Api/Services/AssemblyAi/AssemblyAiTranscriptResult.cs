using System.Text.Json.Serialization;

namespace MeetUp.Api.Services.AssemblyAi;

/// <summary>Shape of AssemblyAI's GET /v2/transcript/{id} response — only the fields we use.</summary>
public sealed class AssemblyAiTranscriptResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>"queued" | "processing" | "completed" | "error".</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("language_code")]
    public string? LanguageCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Only populated when speaker_labels=true was requested and status is "completed".</summary>
    [JsonPropertyName("utterances")]
    public List<AssemblyAiUtterance>? Utterances { get; set; }
}

public sealed class AssemblyAiUtterance
{
    [JsonPropertyName("speaker")]
    public string Speaker { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>Milliseconds from the start of the recording.</summary>
    [JsonPropertyName("start")]
    public int Start { get; set; }

    [JsonPropertyName("end")]
    public int End { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}
