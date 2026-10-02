using System.Net.Http.Json;

namespace MeetUp.Api.Services.AssemblyAi;

/// <summary>
/// Thin wrapper over AssemblyAI's REST API (https://www.assemblyai.com/docs/api-reference).
/// Implemented directly against the HTTP API rather than a third-party SDK package — the
/// surface we need (submit + poll, speaker labels) is small and stable, and this avoids
/// pulling in a dependency whose exact object shapes we'd otherwise have to reverse-engineer.
/// BaseAddress and the Authorization header are configured where this is registered (Program.cs)
/// via the typed-client pattern.
/// </summary>
public sealed class AssemblyAiClient : IAssemblyAiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AssemblyAiClient> _logger;

    public AssemblyAiClient(HttpClient httpClient, ILogger<AssemblyAiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> SubmitTranscriptionAsync(string audioUrl, CancellationToken ct)
    {
        var request = new
        {
            audio_url = audioUrl,
            speaker_labels = true,
            auto_chapters = true,
            entity_detection = true,
            language_detection = true,
        };

        var response = await _httpClient.PostAsJsonAsync("/v2/transcript", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AssemblyAiTranscriptResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("AssemblyAI returned an empty submit response.");

        _logger.LogInformation("Submitted AssemblyAI transcription {TranscriptId} for {AudioUrl}",
            result.Id, audioUrl);

        return result.Id;
    }

    public async Task<string> UploadAsync(Stream media, CancellationToken ct)
    {
        using var content = new StreamContent(media);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

        var response = await _httpClient.PostAsync("/v2/upload", content, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<AssemblyAiUploadResult>(cancellationToken: ct);
        if (string.IsNullOrWhiteSpace(result?.UploadUrl))
        {
            throw new InvalidOperationException("AssemblyAI returned no upload_url.");
        }

        _logger.LogInformation("Uploaded recording to AssemblyAI storage");
        return result.UploadUrl;
    }

    private sealed class AssemblyAiUploadResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("upload_url")]
        public string? UploadUrl { get; set; }
    }

    public async Task<AssemblyAiTranscriptResult> GetTranscriptAsync(string transcriptId, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync($"/v2/transcript/{transcriptId}", ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<AssemblyAiTranscriptResult>(cancellationToken: ct)
            ?? throw new InvalidOperationException("AssemblyAI returned an empty transcript response.");
    }
}
