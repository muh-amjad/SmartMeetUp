using MeetUp.Api.Entities;

namespace MeetUp.Api.Repositories;

public interface IMeetingRepository
{
    Task AddAsync(Meeting meeting, CancellationToken ct);
    Task<Meeting?> GetByIdAsync(Guid id, CancellationToken ct);
    Task UpdateAsync(Meeting meeting, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}