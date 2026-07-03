using MeetUp.Api.Hubs;
using MeetUp.Api.Mappers;
using MeetUp.Api.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace MeetUp.Api.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/[controller]")]
    public class UsersController : ControllerBase
    {
        private readonly IUserRepository _userRepository;

        public UsersController(IUserRepository userRepository)
        {
            _userRepository = userRepository;
        }

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
                    var isOnline = CallHub.TryGetOnlineConnectionByAppUserId(user.Id, out var connectionId);
                    return UserMapper.ToSearchResultDto(user, isOnline, connectionId);
                })
                .ToList();

            return Ok(results);
        }
    }
}
