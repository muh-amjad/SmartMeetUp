using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeetUp.Api.Options;

namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Google Gemini via the generativelanguage REST API. Uses responseMimeType=application/json so the
/// model returns a bare JSON object; the exact shape is pinned by the prompt (shared with the other
/// providers) rather than a responseSchema, to keep one prompt contract across all of them.
/// </summary>
internal sealed class GeminiAnalysisProvider : LlmAnalysisProviderBase
{
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public GeminiAnalysisProvider(
        string key, ProviderConfig config, HttpClient http, ILogger logger)
        : base(key, config, http, logger)
    {
    }

    protected override async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, double temperature, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(Config.BaseUrl) ? DefaultBaseUrl : Config.BaseUrl.TrimEnd('/');
        var url = $"{baseUrl}/models/{Config.Model}:generateContent";

        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = systemPrompt } } },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = userPrompt } } },
            },
            generationConfig = new
            {
                temperature,
                responseMimeType = "application/json",
            },
        };

        // The key goes in a header rather than the query string, so it can never end up in a logged URL.
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        request.Headers.Add("x-goog-api-key", Config.ApiKey);

        using var response = await Http.SendAsync(request, ct);
        await AiHttp.EnsureSuccessAsync(response, $"Gemini ({Config.Model})", ct);

        var body = await response.Content.ReadFromJsonAsync<GenerateContentResponse>(cancellationToken: ct);
        return body?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
    }

    private sealed class GenerateContentResponse
    {
        [JsonPropertyName("candidates")]
        public List<Candidate>? Candidates { get; set; }
    }

    private sealed class Candidate
    {
        [JsonPropertyName("content")]
        public CandidateContent? Content { get; set; }
    }

    private sealed class CandidateContent
    {
        [JsonPropertyName("parts")]
        public List<ContentPart>? Parts { get; set; }
    }

    private sealed class ContentPart
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
