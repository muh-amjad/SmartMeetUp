using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetUp.Api.Options;

namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Serves OpenAI, Groq and OpenRouter. All three speak the same /chat/completions contract and
/// differ only in BaseUrl, model and key, so they share one implementation configured three ways
/// rather than three near-identical classes.
/// </summary>
internal sealed class OpenAiCompatibleAnalysisProvider : LlmAnalysisProviderBase
{
    public OpenAiCompatibleAnalysisProvider(
        string key, ProviderConfig config, HttpClient http, ILogger logger)
        : base(key, config, http, logger)
    {
    }

    protected override async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, double temperature, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Config.BaseUrl!.TrimEnd('/')}/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = Config.Model,
                temperature,
                // json_object rather than a strict json_schema: Groq and OpenRouter don't support
                // strict schemas across all models, and the prompts already pin the shape.
                response_format = new { type = "json_object" },
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt },
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Config.ApiKey);

        using var response = await Http.SendAsync(request, ct);
        await AiHttp.EnsureSuccessAsync(response, $"{Key} ({Config.Model})", ct);

        var body = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(cancellationToken: ct);
        return body?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
    }

    private sealed class ChatCompletionResponse
    {
        [JsonPropertyName("choices")]
        public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")]
        public ChoiceMessage? Message { get; set; }
    }

    private sealed class ChoiceMessage
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
