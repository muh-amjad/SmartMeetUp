using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class SearchRepository : ISearchRepository
{
    private readonly AppDbContext _dbContext;

    public SearchRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ReplaceChunksAsync(Guid meetingId, IEnumerable<TranscriptChunk> chunks, CancellationToken ct)
    {
        var existing = await _dbContext.TranscriptChunks
            .Where(c => c.MeetingId == meetingId)
            .ToListAsync(ct);

        _dbContext.TranscriptChunks.RemoveRange(existing);
        await _dbContext.TranscriptChunks.AddRangeAsync(chunks, ct);
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchKeywordAsync(
        string query, string userId, int limit, CancellationToken ct)
    {
        // websearch_to_tsquery accepts what users actually type (quoted phrases, OR, -exclusions)
        // without throwing on punctuation the way to_tsquery does.
        //
        // The EF.Functions call is inlined deliberately: these are expression-tree stubs, so
        // hoisting one into a local makes EF fall back to client evaluation and the query fails.
        return await Visible(userId)
            .Where(c => c.SearchVector!.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .OrderByDescending(c => c.SearchVector!.Rank(EF.Functions.WebSearchToTsQuery("english", query)))
            .ThenBy(c => c.StartMs)
            .Take(limit)
            .Select(c => new ChunkHit(
                c.Id,
                c.MeetingId,
                c.Meeting!.Title,
                c.Meeting.ActualStartUtc ?? c.Meeting.CreatedUtc,
                c.Text,
                c.StartMs))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChunkHit>> SearchSemanticAsync(
        float[] queryEmbedding, string userId, int limit, CancellationToken ct)
    {
        var vector = new Vector(queryEmbedding);

        return await Visible(userId)
            .Where(c => c.Embedding != null)
            .OrderBy(c => c.Embedding!.CosineDistance(vector))
            .Take(limit)
            .Select(c => new ChunkHit(
                c.Id,
                c.MeetingId,
                c.Meeting!.Title,
                c.Meeting.ActualStartUtc ?? c.Meeting.CreatedUtc,
                c.Text,
                c.StartMs))
            .ToListAsync(ct);
    }

    /// <summary>
    /// The single access-control seam for search: only chunks from meetings the user hosted or
    /// attended. Both arms are built on this, so neither can leak someone else's transcript.
    /// </summary>
    private IQueryable<TranscriptChunk> Visible(string userId) =>
        _dbContext.TranscriptChunks
            .Include(c => c.Meeting)
            .Where(c =>
                c.Meeting!.HostUserId == userId ||
                c.Meeting.Participants.Any(p => p.UserId == userId));

    public Task SaveChangesAsync(CancellationToken ct) => _dbContext.SaveChangesAsync(ct);
}
