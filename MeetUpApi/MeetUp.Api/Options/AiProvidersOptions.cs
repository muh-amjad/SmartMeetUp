namespace MeetUp.Api.Options;

public sealed class AiProvidersOptions
{
    public const string SectionName = "AiProviders";

    public string Default { get; set; } = "gemini-2.5-flash";

    /// <summary>
    /// Gemini model used for semantic-search embeddings. Must support outputDimensionality, because
    /// transcript_chunks stores 768-dimension vectors (text-embedding-004, the original model, has
    /// been retired and now returns 404).
    /// </summary>
    public string EmbeddingModel { get; set; } = "gemini-embedding-001";

    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

public sealed class ProviderConfig
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Env-injected. Empty means the provider is disabled and hidden from the picker.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>Set for OpenAI-compatible providers (OpenAI, Groq, OpenRouter). Null selects Gemini.</summary>
    public string? BaseUrl { get; set; }

    public bool IsFree { get; set; }

    public int ContextWindow { get; set; }
}
