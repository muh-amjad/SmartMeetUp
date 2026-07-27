using System.Security.Claims;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Infrastructure.Exceptions;
using MeetUp.Api.Services.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/search")]
public class SearchController : ControllerBase
{
    private const int MaxLimit = 50;

    private readonly ISearchService _searchService;

    public SearchController(ISearchService searchService)
    {
        _searchService = searchService;
    }

    /// <summary>
    /// Searches the caller's own meeting transcripts. Results are always scoped to meetings they
    /// hosted or attended.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SearchResultDto>>> Search(
        // Nullable so that a missing or blank query reaches the check below and returns the same
        // validation shape as every other bad input, instead of model binding's implicit-required 400.
        [FromQuery] string? q,
        [FromQuery] string mode = "hybrid",
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        if (string.IsNullOrWhiteSpace(q))
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(q), ["A search query is required."]);
        }

        if (!Enum.TryParse<SearchMode>(mode, ignoreCase: true, out var searchMode))
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(mode), [$"Unknown mode '{mode}'. Expected keyword, semantic or hybrid."]);
        }

        var results = await _searchService.SearchAsync(
            q, userId, searchMode, Math.Clamp(limit, 1, MaxLimit), ct);

        return Ok(results.Select(r => new SearchResultDto
        {
            MeetingId = r.MeetingId,
            MeetingTitle = r.MeetingTitle,
            MeetingDate = r.MeetingDate,
            Snippet = r.Snippet,
            StartMs = r.StartMs,
            Score = r.Score,
        }).ToList());
    }
}
