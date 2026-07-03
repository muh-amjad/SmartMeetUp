using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories
{
    public interface IUserRepository
    {
        Task<IReadOnlyList<ApplicationUser>> SearchUsersAsync(string query, string excludeUserId, CancellationToken cancellationToken);
        Task<ApplicationUser?> FindByUsernameOrEmailAsync(string usernameOrEmail, CancellationToken cancellationToken);
    }
}
