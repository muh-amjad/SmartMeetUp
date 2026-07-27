namespace MeetUp.Api.Services.Search;

public enum SearchMode
{
    Keyword = 0,
    Semantic = 1,
    Hybrid = 2,
}

public sealed class SearchResult
{
    public Guid MeetingId { get; set; }
    public string MeetingTitle { get; set; } = string.Empty;
    public DateTime MeetingDate { get; set; }

    /// <summary>Text around the match, trimmed for display.</summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>Offset into the recording, for jump-to-moment.</summary>
    public int StartMs { get; set; }

    public double Score { get; set; }
}

public interface ISearchService
{
    Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, string requestingUserId, SearchMode mode, int limit, CancellationToken ct);
}
