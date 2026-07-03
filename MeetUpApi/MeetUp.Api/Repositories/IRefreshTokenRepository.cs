using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories
{
    public interface IRefreshTokenRepository
    {
        Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken);
        Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken);
        Task SaveChangesAsync(CancellationToken cancellationToken);
        Task RevokeUserTokensAsync(string userId, CancellationToken cancellationToken);
    }
}
