using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services.Ai;

public sealed record AnalysisProviderInfo(
    string Key,
    string DisplayName,
    bool IsFree,
    bool IsDefault,
    int ContextWindow);

/// <summary>
/// Builds one provider instance per configured entry that has an API key. Providers without a key
/// are treated as not installed: they never appear in the picker and can never be selected.
/// </summary>
public sealed class AnalysisProviderRegistry
{
    private readonly Dictionary<string, IAnalysisProvider> _providers;
    private readonly AiProvidersOptions _options;

    public AnalysisProviderRegistry(
        IOptions<AiProvidersOptions> options,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _providers = new Dictionary<string, IAnalysisProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, config) in _options.Providers)
        {
            if (string.IsNullOrWhiteSpace(config.ApiKey))
            {
                continue;
            }

            var http = httpClientFactory.CreateClient("ai-analysis");

            // A BaseUrl means an OpenAI-compatible endpoint (OpenAI, Groq, OpenRouter);
            // its absence means Gemini, which has its own request shape.
            IAnalysisProvider provider = string.IsNullOrWhiteSpace(config.BaseUrl)
                ? new GeminiAnalysisProvider(key, config, http, loggerFactory.CreateLogger<GeminiAnalysisProvider>())
                : new OpenAiCompatibleAnalysisProvider(key, config, http, loggerFactory.CreateLogger<OpenAiCompatibleAnalysisProvider>());

            _providers[key] = provider;
        }
    }

    public string DefaultKey => _options.Default;

    public bool IsAvailable(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _providers.ContainsKey(key);

    public IAnalysisProvider? Find(string? key) =>
        !string.IsNullOrWhiteSpace(key) && _providers.TryGetValue(key, out var provider) ? provider : null;

    public IReadOnlyList<AnalysisProviderInfo> GetAvailable() =>
        _providers
            .Select(pair => new AnalysisProviderInfo(
                pair.Key,
                pair.Value.DisplayName,
                _options.Providers[pair.Key].IsFree,
                string.Equals(pair.Key, _options.Default, StringComparison.OrdinalIgnoreCase),
                _options.Providers[pair.Key].ContextWindow))
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
