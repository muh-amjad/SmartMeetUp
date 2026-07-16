using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class ChatMessageRepository : IChatMessageRepository
{
    private readonly AppDbContext _dbContext;

    public ChatMessageRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(ChatMessage message, CancellationToken ct)
    {
        await _dbContext.ChatMessages.AddAsync(message, ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> GetByMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        return await _dbContext.ChatMessages
            .Include(m => m.Sender)
            .Where(m => m.MeetingId == meetingId)
            .OrderBy(m => m.SentUtc)
            .ToListAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct)
    {
        return _dbContext.SaveChangesAsync(ct);
    }
}