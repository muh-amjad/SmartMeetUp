namespace MeetUp.Api.Options;

public sealed class AiProvidersOptions
{
    public const string SectionName = "AiProviders";

    public string Default { get; set; } = "gemini-2.5-flash";

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
