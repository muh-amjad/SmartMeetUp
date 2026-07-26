using System.Security.Claims;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Infrastructure.Exceptions;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services.Ai;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/me")]
public class UserPreferencesController : ControllerBase
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AnalysisProviderRegistry _registry;
    private readonly IMeetingAnalyticsRepository _analyticsRepository;
    private readonly IMeetingAnalysisRepository _analysisRepository;

    public UserPreferencesController(
        UserManager<ApplicationUser> userManager,
        AnalysisProviderRegistry registry,
        IMeetingAnalyticsRepository analyticsRepository,
        IMeetingAnalysisRepository analysisRepository)
    {
        _userManager = userManager;
        _registry = registry;
        _analyticsRepository = analyticsRepository;
        _analysisRepository = analysisRepository;
    }

    /// <summary>Account-wide meeting analytics for the caller over the given window (defaults to 90 days).</summary>
    [HttpGet("analytics")]
    public async Task<ActionResult<AccountAnalyticsDto>> GetAnalytics(
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var toUtc = to ?? DateTime.UtcNow;
        var fromUtc = from ?? toUtc.AddDays(-90);

        var analytics = await _analyticsRepository.GetAnalyticsForUserAsync(userId, fromUtc, toUtc, ct);

        var totalSeconds = analytics.Sum(a => (long)a.TotalDurationSeconds);
        var speakingSeconds = analytics
            .SelectMany(a => a.SpeakingDistribution)
            .Where(s => s.UserId == userId)
            .Sum(s => (long)s.Seconds);

        var weekly = analytics
            .GroupBy(a => StartOfWeekUtc(a.Meeting!.CreatedUtc))
            .Select(g => new WeeklyBucketDto
            {
                WeekStartUtc = g.Key,
                MeetingCount = g.Count(),
                TotalMinutes = (int)Math.Round(g.Sum(a => a.TotalDurationSeconds) / 60.0),
            })
            .OrderBy(w => w.WeekStartUtc)
            .ToList();

        var topics = await CollectTopTopicsAsync(analytics.Select(a => a.MeetingId), ct);

        return Ok(new AccountAnalyticsDto
        {
            TotalMeetingHours = Math.Round(totalSeconds / 3600.0, 1),
            MeetingCount = analytics.Count,
            AverageDurationMinutes = analytics.Count > 0
                ? (int)Math.Round(totalSeconds / 60.0 / analytics.Count)
                : 0,
            TotalSpeakingSeconds = (int)speakingSeconds,
            MostDiscussedTopics = topics,
            WeeklyBreakdown = weekly,
        });
    }

    /// <summary>Most frequent key topics across the summaries of the meetings in range.</summary>
    private async Task<IReadOnlyList<string>> CollectTopTopicsAsync(
        IEnumerable<Guid> meetingIds, CancellationToken ct)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var meetingId in meetingIds)
        {
            var summary = await _analysisRepository.GetSummaryAsync(meetingId, ct);
            foreach (var topic in summary?.KeyTopics ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(topic))
                {
                    continue;
                }

                var key = topic.Trim();
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        return counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(pair => pair.Key)
            .ToList();
    }

    private static DateTime StartOfWeekUtc(DateTime value)
    {
        var date = DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday);
    }

    [HttpGet("preferences")]
    public async Task<ActionResult<UserPreferencesDto>> GetPreferences()
    {
        var user = await GetCurrentUserAsync();

        return Ok(new UserPreferencesDto
        {
            PreferredAnalysisProviderKey = user.PreferredAnalysisProviderKey,
        });
    }

    [HttpPatch("preferences")]
    public async Task<IActionResult> UpdatePreferences([FromBody] UserPreferencesDto request)
    {
        var user = await GetCurrentUserAsync();
        var requestedKey = request.PreferredAnalysisProviderKey;

        // Null/empty means "use the system default"; anything else must be a provider that exists
        // and has a key, otherwise the preference would silently never apply.
        if (string.IsNullOrWhiteSpace(requestedKey))
        {
            user.PreferredAnalysisProviderKey = null;
        }
        else if (_registry.IsAvailable(requestedKey))
        {
            user.PreferredAnalysisProviderKey = requestedKey;
        }
        else
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(request.PreferredAnalysisProviderKey),
                [$"'{requestedKey}' is not an available AI provider."]);
        }

        await _userManager.UpdateAsync(user);
        return NoContent();
    }

    private async Task<ApplicationUser> GetCurrentUserAsync()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        return await _userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("User not found.");
    }
}
