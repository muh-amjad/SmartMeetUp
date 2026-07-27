namespace MeetUp.Api.Services.Ai;

public interface IEmbeddingService
{
    /// <summary>Vector length this service produces; must match the vector(N) column.</summary>
    int Dimensions { get; }

    /// <summary>False when no embedding API key is configured, so callers can skip semantic work.</summary>
    bool IsConfigured { get; }

    /// <summary>Embeds a batch of texts, returning one vector per input in the same order.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
