using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class MeetingParticipantRepository : IMeetingParticipantRepository
{
    private readonly AppDbContext _dbContext;

    public MeetingParticipantRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(MeetingParticipant participant, CancellationToken ct)
    {
        await _dbContext.MeetingParticipants.AddAsync(participant, ct);
    }

    public Task<MeetingParticipant?> GetByMeetingAndUserAsync(Guid meetingId, string userId, CancellationToken ct)
    {
        return _dbContext.MeetingParticipants
            .FirstOrDefaultAsync(p => p.MeetingId == meetingId && p.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<MeetingParticipant>> GetByMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        return await _dbContext.MeetingParticipants
            .Include(p => p.User)
            .Where(p => p.MeetingId == meetingId)
            .OrderBy(p => p.JoinedUtc ?? DateTime.MinValue)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}