using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories
{
    public class UserRepository : IUserRepository
    {
        private readonly AppDbContext _dbContext;

        public UserRepository(AppDbContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task<IReadOnlyList<ApplicationUser>> SearchUsersAsync(string query, string excludeUserId, CancellationToken cancellationToken)
        {
            var normalized = query.Trim().ToLowerInvariant();

            return await _dbContext.Users
                .Where(u => u.Id != excludeUserId)
                .Where(u =>
                    u.UserName != null && u.UserName.ToLower().Contains(normalized)
                    || u.Email != null && u.Email.ToLower().Contains(normalized))
                .OrderBy(u => u.UserName)
                .Take(20)
                .ToListAsync(cancellationToken);
        }

        public Task<ApplicationUser?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken cancellationToken)
        {
            var normalized = usernameOrEmail.Trim().ToLowerInvariant();

            return _dbContext.Users.FirstOrDefaultAsync(
                u => u.UserName != null && u.UserName.ToLower() == normalized
                    || u.Email != null && u.Email.ToLower() == normalized,
                cancellationToken);
        }
    }
}
