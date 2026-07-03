using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Entities;

namespace MeetUp.Api.Services
{
    public interface ITokenService
    {
        AuthResponseDto CreateAuthResponse(ApplicationUser user, string refreshToken);
        string GenerateRefreshToken();
    }
}
