using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class TranscriptRepository : ITranscriptRepository
{
    private readonly AppDbContext _dbContext;

    public TranscriptRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<Transcript?> GetByMeetingIdAsync(Guid meetingId, CancellationToken ct)
    {
        return _dbContext.Transcripts
            .Include(t => t.Utterances.OrderBy(u => u.StartMs))
                .ThenInclude(u => u.Participant)
            .FirstOrDefaultAsync(t => t.MeetingId == meetingId, ct);
    }

    public async Task AddAsync(Transcript transcript, CancellationToken ct)
    {
        await _dbContext.Transcripts.AddAsync(transcript, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}
