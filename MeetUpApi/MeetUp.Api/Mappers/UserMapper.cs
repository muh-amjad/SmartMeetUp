using MeetUp.Api.Dtos.Users;
using MeetUp.Api.Entities;

namespace MeetUp.Api.Mappers
{
    public static class UserMapper
    {
        public static UserSearchResultDto ToSearchResultDto(ApplicationUser user, bool isOnline, string? connectionId)
        {
            return new UserSearchResultDto
            {
                UserId = user.Id,
                Username = user.UserName ?? string.Empty,
                Email = user.Email ?? string.Empty,
                IsOnline = isOnline,
                ConnectionId = connectionId,
            };
        }
    }
}
