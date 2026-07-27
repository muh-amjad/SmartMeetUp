using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

/// <summary>A chunk hit from one arm of search, already ordered best-first by that arm.</summary>
public sealed record ChunkHit(
    Guid ChunkId,
    Guid MeetingId,
    string MeetingTitle,
    DateTime MeetingDate,
    string Text,
    int StartMs);

public interface ISearchRepository
{
    Task ReplaceChunksAsync(Guid meetingId, IEnumerable<TranscriptChunk> chunks, CancellationToken ct);

    /// <summary>
    /// Keyword arm: PostgreSQL full-text search over each chunk's generated tsvector, ranked by
    /// ts_rank. Scoped to meetings the user hosted or attended.
    /// </summary>
    Task<IReadOnlyList<ChunkHit>> SearchKeywordAsync(
        string query, string userId, int limit, CancellationToken ct);

    /// <summary>
    /// Semantic arm: nearest neighbours by cosine distance against the query embedding. Scoped the
    /// same way as the keyword arm.
    /// </summary>
    Task<IReadOnlyList<ChunkHit>> SearchSemanticAsync(
        float[] queryEmbedding, string userId, int limit, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}
