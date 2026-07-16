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

    public Task DeleteAsync(Meeting meeting, CancellationToken ct)
    {
        _dbContext.Meetings.Remove(meeting);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Meeting>> GetUserMeetingsAsync(
        string userId,
        MeetingStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        // "My meetings" = meetings I hosted OR meetings I participated in
        var query = _dbContext.Meetings
            .Include(m => m.Host)
            .Include(m => m.Participants)
            .Where(m =>
                m.HostUserId == userId ||
                m.Participants.Any(p => p.UserId == userId));

        if (status.HasValue)
        {
            query = query.Where(m => m.Status == status.Value);
        }

        return await query
            .OrderByDescending(m => m.CreatedUtc)
            .Skip(Math.Max(0, page - 1) * pageSize)
            .Take(Math.Clamp(pageSize, 1, 100))
            .ToListAsync(ct);
    }
}