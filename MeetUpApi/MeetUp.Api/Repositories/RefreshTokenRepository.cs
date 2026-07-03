using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories
{
    public class RefreshTokenRepository : IRefreshTokenRepository
    {
        private readonly AppDbContext _dbContext;

        public RefreshTokenRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken)
        {
            await _dbContext.RefreshTokens.AddAsync(refreshToken, cancellationToken);
        }

        public Task<RefreshToken?> GetByTokenAsync(string token, CancellationToken cancellationToken)
        {
            return _dbContext.RefreshTokens
                .Include(refresh => refresh.User)
                .FirstOrDefaultAsync(refresh => refresh.Token == token, cancellationToken);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            return _dbContext.SaveChangesAsync(cancellationToken);
        }

        public async Task RevokeUserTokensAsync(string userId, CancellationToken cancellationToken)
        {
            var activeTokens = await _dbContext.RefreshTokens
                .Where(refresh => refresh.UserId == userId && refresh.RevokedAtUtc == null && refresh.ExpiresAtUtc > DateTime.UtcNow)
                .ToListAsync(cancellationToken);

            foreach (var token in activeTokens)
            {
                token.RevokedAtUtc = DateTime.UtcNow;
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}
