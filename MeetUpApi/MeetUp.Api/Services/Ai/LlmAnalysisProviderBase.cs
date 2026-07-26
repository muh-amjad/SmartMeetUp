using System.Globalization;
using System.Text.Json;
using MeetUp.Api.Options;

namespace MeetUp.Api.Services.Ai;

/// <summary>
/// Everything except "send these prompts to the model and give me back JSON" is identical across
/// providers, so the four analysis operations live here and each concrete provider only implements
/// the transport.
/// </summary>
internal abstract class LlmAnalysisProviderBase : IAnalysisProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    protected readonly ProviderConfig Config;
    protected readonly HttpClient Http;
    private readonly ILogger _logger;

    protected LlmAnalysisProviderBase(string key, ProviderConfig config, HttpClient http, ILogger logger)
    {
        Key = key;
        Config = config;
        Http = http;
        _logger = logger;
    }

    public string Key { get; }
    public string DisplayName => Config.DisplayName;
    public string ModelId => Config.Model;

    /// <summary>Sends the prompts and returns the model's raw reply, expected to be a JSON object.</summary>
    protected abstract Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, double temperature, CancellationToken ct);

    public async Task<SummaryResult> GenerateSummaryAsync(string transcript, CancellationToken ct)
    {
        var raw = await CompleteAsync(
            AnalysisPrompts.SystemPrompt,
            AnalysisPrompts.Summary(FitToContext(transcript)),
            AnalysisPrompts.ExtractionTemperature,
            ct);

        var payload = Parse<SummaryPayload>(raw);
        return new SummaryResult
        {
            Overview = payload?.Overview?.Trim() ?? string.Empty,
            KeyTopics = payload?.KeyTopics?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>(),
        };
    }

    public async Task<ActionItemResult[]> ExtractActionItemsAsync(
        string transcript, IEnumerable<ParticipantInfo> participants, CancellationToken ct)
    {
        var raw = await CompleteAsync(
            AnalysisPrompts.SystemPrompt,
            AnalysisPrompts.ActionItems(FitToContext(transcript), participants),
            AnalysisPrompts.ExtractionTemperature,
            ct);

        var payload = Parse<ActionItemsPayload>(raw);
        return (payload?.ActionItems ?? new List<ActionItemPayload>())
            .Where(a => !string.IsNullOrWhiteSpace(a.Description))
            .Select(a => new ActionItemResult
            {
                Description = a.Description!.Trim(),
                AssigneeNameRaw = string.IsNullOrWhiteSpace(a.AssigneeName) ? null : a.AssigneeName.Trim(),
                DueDateUtc = ParseDueDate(a.DueDate),
            })
            .ToArray();
    }

    public async Task<DecisionResult[]> ExtractDecisionsAsync(string transcript, CancellationToken ct)
    {
        var raw = await CompleteAsync(
            AnalysisPrompts.SystemPrompt,
            AnalysisPrompts.Decisions(FitToContext(transcript)),
            AnalysisPrompts.ExtractionTemperature,
            ct);

        var payload = Parse<DecisionsPayload>(raw);
        return (payload?.Decisions ?? new List<DecisionPayload>())
            .Where(d => !string.IsNullOrWhiteSpace(d.Description))
            .Select(d => new DecisionResult { Description = d.Description!.Trim() })
            .ToArray();
    }

    public async Task<EmailDraftResult> DraftFollowUpEmailAsync(MeetingContext meeting, CancellationToken ct)
    {
        var fitted = new MeetingContext
        {
            Title = meeting.Title,
            HeldUtc = meeting.HeldUtc,
            Participants = meeting.Participants,
            Transcript = FitToContext(meeting.Transcript),
        };

        var raw = await CompleteAsync(
            AnalysisPrompts.SystemPrompt,
            AnalysisPrompts.FollowUpEmail(fitted),
            AnalysisPrompts.DraftingTemperature,
            ct);

        var payload = Parse<EmailPayload>(raw);
        return new EmailDraftResult
        {
            Subject = payload?.Subject?.Trim() is { Length: > 0 } s ? s : $"Follow-up: {meeting.Title}",
            BodyMarkdown = payload?.BodyMarkdown?.Trim() ?? string.Empty,
        };
    }

    /// <summary>
    /// Keeps the transcript inside the model's context window. The plan calls for chunk-and-merge
    /// on small-context models; this trims instead and says so in the log rather than silently
    /// dropping the tail of a long meeting.
    /// </summary>
    private string FitToContext(string transcript)
    {
        // ~4 characters per token, leaving ~40% of the window for the prompt and the reply.
        var budget = Math.Max(4_000, (int)(Config.ContextWindow * 4 * 0.6));
        if (transcript.Length <= budget)
        {
            return transcript;
        }

        _logger.LogWarning(
            "Transcript truncated from {OriginalChars} to {BudgetChars} chars for provider {ProviderKey} " +
            "(context window {ContextWindow} tokens) — the end of the meeting was not analysed",
            transcript.Length, budget, Key, Config.ContextWindow);

        return transcript[..budget];
    }

    private T? Parse<T>(string raw) where T : class
    {
        var json = ExtractJsonObject(raw);
        if (json is null)
        {
            _logger.LogWarning("Provider {ProviderKey} returned no JSON object. Raw reply: {Raw}", Key, Truncate(raw));
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Provider {ProviderKey} returned malformed JSON: {Raw}", Key, Truncate(json));
            return null;
        }
    }

    /// <summary>
    /// Models routinely wrap JSON in markdown fences or add a sentence around it despite being told
    /// not to, so take the outermost {...} instead of trusting the reply to be bare JSON.
    /// </summary>
    private static string? ExtractJsonObject(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : null;
    }

    private static DateTime? ParseDueDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static string Truncate(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
