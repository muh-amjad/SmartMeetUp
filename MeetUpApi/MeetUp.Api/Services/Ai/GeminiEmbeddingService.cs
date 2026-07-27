using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Google text-embedding-004 via the generativelanguage REST API. Reuses the Gemini API key already
/// configured for analysis, so enabling semantic search needs no extra credentials.
/// Implemented against REST for the same reason as the analysis providers — a small, stable surface
/// is cheaper to own than an SDK whose object shapes have to be reverse-engineered.
/// </summary>
public sealed class GeminiEmbeddingService : IEmbeddingService
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";
    private const string Model = "text-embedding-004";

    /// <summary>Gemini caps batchEmbedContents at 100 requests per call.</summary>
    private const int MaxBatchSize = 100;

    private readonly HttpClient _http;
    private readonly ILogger<GeminiEmbeddingService> _logger;
    private readonly string _apiKey;

    public GeminiEmbeddingService(
        HttpClient http,
        IOptions<AiProvidersOptions> aiProviders,
        ILogger<GeminiEmbeddingService> logger)
    {
        _http = http;
        _logger = logger;

        // Any configured Gemini provider entry will do — they all carry the same Google API key.
        _apiKey = aiProviders.Value.Providers
            .Where(p => string.IsNullOrWhiteSpace(p.Value.BaseUrl) && !string.IsNullOrWhiteSpace(p.Value.ApiKey))
            .Select(p => p.Value.ApiKey)
            .FirstOrDefault() ?? string.Empty;
    }

    public int Dimensions => 768;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("No Gemini API key is configured, so embeddings cannot be generated.");
        }

        if (texts.Count == 0)
        {
            return Array.Empty<float[]>();
        }

        var results = new List<float[]>(texts.Count);

        foreach (var batch in Chunk(texts, MaxBatchSize))
        {
            var payload = new
            {
                requests = batch.Select(text => new
                {
                    model = $"models/{Model}",
                    content = new { parts = new[] { new { text } } },
                }).ToArray(),
            };

            var url = $"{BaseUrl}/models/{Model}:batchEmbedContents?key={_apiKey}";
            var response = await _http.PostAsJsonAsync(url, payload, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<BatchEmbedResponse>(cancellationToken: ct);
            var embeddings = body?.Embeddings ?? new List<EmbeddingValues>();

            if (embeddings.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Gemini returned {embeddings.Count} embeddings for {batch.Count} inputs.");
            }

            foreach (var embedding in embeddings)
            {
                var values = embedding.Values ?? new List<float>();
                if (values.Count != Dimensions)
                {
                    throw new InvalidOperationException(
                        $"Expected {Dimensions}-dimension embeddings but got {values.Count}.");
                }

                results.Add(values.ToArray());
            }
        }

        _logger.LogDebug("Embedded {Count} text(s) with {Model}", results.Count, Model);
        return results;
    }

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            yield return source.Skip(i).Take(size).ToList();
        }
    }

    private sealed class BatchEmbedResponse
    {
        [JsonPropertyName("embeddings")]
        public List<EmbeddingValues>? Embeddings { get; set; }
    }

    private sealed class EmbeddingValues
    {
        [JsonPropertyName("values")]
        public List<float>? Values { get; set; }
    }
}
