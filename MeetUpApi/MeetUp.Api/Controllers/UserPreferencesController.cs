using System.Security.Claims;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Infrastructure.Exceptions;
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

    public UserPreferencesController(
        UserManager<ApplicationUser> userManager,
        AnalysisProviderRegistry registry)
    {
        _userManager = userManager;
        _registry = registry;
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
