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

    /// <summary>
    /// Action items across all the caller's meetings.
    /// </summary>
    /// <param name="filter">all (default) · open · done · overdue · mine</param>
    [HttpGet("action-items")]
    public async Task<ActionResult<IReadOnlyList<ActionItemWithMeetingDto>>> GetActionItems(
        [FromQuery] string filter = "all", CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var items = await _analysisRepository.GetActionItemsForUserAsync(userId, ct);
        var now = DateTime.UtcNow;

        var mapped = items.Select(item => new ActionItemWithMeetingDto
        {
            Id = item.Id,
            MeetingId = item.MeetingId,
            MeetingTitle = item.Meeting?.Title ?? string.Empty,
            MeetingDate = item.Meeting?.ActualStartUtc ?? item.Meeting?.CreatedUtc ?? item.CreatedUtc,
            Description = item.Description,
            AssigneeUserId = item.AssigneeUserId,
            AssigneeUsername = item.Assignee?.DisplayName is { Length: > 0 } name
                ? name
                : item.Assignee?.UserName,
            AssigneeNameRaw = item.AssigneeNameRaw,
            DueDateUtc = item.DueDateUtc,
            Status = item.Status.ToString(),
            IsOverdue = item.Status == ActionItemStatus.Open
                        && item.DueDateUtc.HasValue
                        && item.DueDateUtc.Value < now,
            IsAssignedToMe = item.AssigneeUserId == userId,
            CompletedUtc = item.CompletedUtc,
        });

        mapped = filter.ToLowerInvariant() switch
        {
            "open" => mapped.Where(i => i.Status == nameof(ActionItemStatus.Open)),
            "done" => mapped.Where(i => i.Status == nameof(ActionItemStatus.Done)),
            "overdue" => mapped.Where(i => i.IsOverdue),
            "mine" => mapped.Where(i => i.IsAssignedToMe),
            "all" => mapped,
            _ => throw new Infrastructure.Exceptions.ValidationException(
                nameof(filter), [$"Unknown filter '{filter}'. Expected all, open, done, overdue or mine."]),
        };

        return Ok(mapped.ToList());
    }

    [HttpGet("profile")]
    public async Task<ActionResult<ProfileDto>> GetProfile()
    {
        var user = await GetCurrentUserAsync();

        return Ok(new ProfileDto
        {
            UserId = user.Id,
            Username = user.UserName ?? string.Empty,
            Email = user.Email ?? string.Empty,
            DisplayName = user.DisplayName,
        });
    }

    [HttpPatch("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequestDto request)
    {
        var user = await GetCurrentUserAsync();

        var displayName = request.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is 0 or > 120)
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(request.DisplayName), ["Display name must be between 1 and 120 characters."]);
        }

        user.DisplayName = displayName;
        var result = await _userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(request.DisplayName), result.Errors.Select(e => e.Description).ToArray());
        }

        return NoContent();
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request)
    {
        var user = await GetCurrentUserAsync();

        // Identity verifies the current password itself, so a wrong one surfaces as a validation
        // error rather than silently succeeding.
        var result = await _userManager.ChangePasswordAsync(
            user, request.CurrentPassword ?? string.Empty, request.NewPassword ?? string.Empty);

        if (!result.Succeeded)
        {
            throw new Infrastructure.Exceptions.ValidationException(
                nameof(request.NewPassword), result.Errors.Select(e => e.Description).ToArray());
        }

        return NoContent();
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
            OptOutFollowUpEmails = user.OptOutFollowUpEmails,
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

        // Omitted entirely means "leave as is", so only assign when the caller sent a value.
        if (request.OptOutFollowUpEmails.HasValue)
        {
            user.OptOutFollowUpEmails = request.OptOutFollowUpEmails.Value;
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
