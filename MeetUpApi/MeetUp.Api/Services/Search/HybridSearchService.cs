using MeetUp.Api.Repositories;
using MeetUp.Api.Services.Ai;

namespace MeetUp.Api.Services.Search;

/// <summary>
/// Runs the keyword and semantic arms and merges them with Reciprocal Rank Fusion.
///
/// RRF combines the two arms by *rank* rather than score, which matters because ts_rank and cosine
/// distance are on entirely different scales and could not be added together meaningfully.
/// </summary>
public sealed class HybridSearchService : ISearchService
{
    /// <summary>Standard RRF damping constant: keeps any single arm's top hit from dominating.</summary>
    private const int RrfK = 60;

    /// <summary>Each arm fetches deeper than the requested limit so fusion has room to reorder.</summary>
    private const int ArmDepth = 20;

    private const int SnippetLength = 300;

    private readonly ISearchRepository _searchRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<HybridSearchService> _logger;

    public HybridSearchService(
        ISearchRepository searchRepository,
        IEmbeddingService embeddingService,
        ILogger<HybridSearchService> logger)
    {
        _searchRepository = searchRepository;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, string requestingUserId, SearchMode mode, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<SearchResult>();
        }

        var depth = Math.Max(ArmDepth, limit);

        // Semantic needs an embedding key. Rather than return nothing when it is missing, fall back
        // to keyword so search still works on a deployment with no AI credentials.
        var wantsSemantic = mode is SearchMode.Semantic or SearchMode.Hybrid;
        var semanticAvailable = wantsSemantic && _embeddingService.IsConfigured;

        if (wantsSemantic && !semanticAvailable)
        {
            _logger.LogInformation(
                "Search: semantic mode requested but no embedding key is configured — using keyword only");
        }

        var keywordHits = mode is SearchMode.Keyword or SearchMode.Hybrid || !semanticAvailable
            ? await _searchRepository.SearchKeywordAsync(query, requestingUserId, depth, ct)
            : Array.Empty<ChunkHit>();

        var semanticHits = Array.Empty<ChunkHit>() as IReadOnlyList<ChunkHit>;
        if (semanticAvailable)
        {
            try
            {
                var embeddings = await _embeddingService.EmbedBatchAsync(new[] { query }, ct);
                semanticHits = await _searchRepository.SearchSemanticAsync(
                    embeddings[0], requestingUserId, depth, ct);
            }
            catch (Exception ex)
            {
                // A provider outage degrades search to keyword rather than failing the request.
                _logger.LogWarning(ex, "Search: embedding the query failed — falling back to keyword results");
                if (keywordHits.Count == 0)
                {
                    keywordHits = await _searchRepository.SearchKeywordAsync(query, requestingUserId, depth, ct);
                }
            }
        }

        return Fuse(keywordHits, semanticHits, limit);
    }

    private static IReadOnlyList<SearchResult> Fuse(
        IReadOnlyList<ChunkHit> keywordHits, IReadOnlyList<ChunkHit> semanticHits, int limit)
    {
        var scores = new Dictionary<Guid, double>();
        var hitsById = new Dictionary<Guid, ChunkHit>();

        void Accumulate(IReadOnlyList<ChunkHit> hits)
        {
            for (var rank = 0; rank < hits.Count; rank++)
            {
                var hit = hits[rank];
                hitsById[hit.ChunkId] = hit;
                scores[hit.ChunkId] = scores.GetValueOrDefault(hit.ChunkId) + 1.0 / (rank + RrfK);
            }
        }

        Accumulate(keywordHits);
        Accumulate(semanticHits);

        return scores
            .OrderByDescending(pair => pair.Value)
            .Take(limit)
            .Select(pair =>
            {
                var hit = hitsById[pair.Key];
                return new SearchResult
                {
                    MeetingId = hit.MeetingId,
                    MeetingTitle = hit.MeetingTitle,
                    MeetingDate = hit.MeetingDate,
                    Snippet = Snip(hit.Text),
                    StartMs = hit.StartMs,
                    Score = Math.Round(pair.Value, 6),
                };
            })
            .ToList();
    }

    private static string Snip(string text) =>
        text.Length <= SnippetLength ? text : text[..SnippetLength].TrimEnd() + "…";
}
