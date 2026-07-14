using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class MeetingRepository : IMeetingRepository
{
    private readonly AppDbContext _dbContext;

    public MeetingRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Meeting meeting, CancellationToken ct)
    {
        await _dbContext.Meetings.AddAsync(meeting, ct);
    }

    public Task<Meeting?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return _dbContext.Meetings
            .Include(m => m.Host)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public Task UpdateAsync(Meeting meeting, CancellationToken ct)
    {
        _dbContext.Meetings.Update(meeting);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}