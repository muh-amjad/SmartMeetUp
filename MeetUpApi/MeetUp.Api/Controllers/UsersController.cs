using MeetUp.Api.Mappers;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MeetUp.Api.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class UsersController(IUserRepository userRepository, IPresenceTracker presenceTracker) : ControllerBase
    {
        private readonly IUserRepository _userRepository = userRepository;
        private readonly IPresenceTracker _presenceTracker = presenceTracker;

        [HttpGet("search")]
        public async Task<IActionResult> Search([FromQuery] string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return Ok(Array.Empty<object>());
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Unauthorized();
            }

            var users = await _userRepository.SearchUsersAsync(query, userId, cancellationToken);
            var results = users
                .Select(user =>
                {
                    var isOnline = _presenceTracker.TryGetConnectionByAppUserId(user.Id, out var connectionId);
                    return UserMapper.ToSearchResultDto(user, isOnline, connectionId);
                })
                .ToList();

            return Ok(results);
        }
    }
}
