using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class MeetingAnalysisRepository : IMeetingAnalysisRepository
{
    private readonly AppDbContext _dbContext;

    public MeetingAnalysisRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<MeetingSummary?> GetSummaryAsync(Guid meetingId, CancellationToken ct) =>
        _dbContext.MeetingSummaries.FirstOrDefaultAsync(s => s.MeetingId == meetingId, ct);

    public async Task<IReadOnlyList<ActionItem>> GetActionItemsAsync(Guid meetingId, CancellationToken ct) =>
        await _dbContext.ActionItems
            .Include(a => a.Assignee)
            .Include(a => a.SourceUtterance)
            .Where(a => a.MeetingId == meetingId)
            .OrderBy(a => a.CreatedUtc)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ActionItem>> GetActionItemsForUserAsync(string userId, CancellationToken ct) =>
        await _dbContext.ActionItems
            .Include(a => a.Assignee)
            .Include(a => a.Meeting)
            .Where(a =>
                a.Meeting!.HostUserId == userId ||
                a.Meeting.Participants.Any(p => p.UserId == userId))
            // Open work first, then soonest due, then newest — the order the UI wants by default.
            .OrderBy(a => a.Status)
            .ThenBy(a => a.DueDateUtc ?? DateTime.MaxValue)
            .ThenByDescending(a => a.CreatedUtc)
            .ToListAsync(ct);

    public Task<ActionItem?> GetActionItemAsync(Guid actionItemId, CancellationToken ct) =>
        _dbContext.ActionItems
            .Include(a => a.Meeting)
            .Include(a => a.Assignee)
            .FirstOrDefaultAsync(a => a.Id == actionItemId, ct);

    public async Task<IReadOnlyList<Decision>> GetDecisionsAsync(Guid meetingId, CancellationToken ct) =>
        await _dbContext.Decisions
            .Include(d => d.SourceUtterance)
            .Where(d => d.MeetingId == meetingId)
            .OrderBy(d => d.CreatedUtc)
            .ToListAsync(ct);

    public Task<FollowUpEmail?> GetFollowUpEmailAsync(Guid meetingId, CancellationToken ct) =>
        _dbContext.FollowUpEmails.FirstOrDefaultAsync(e => e.MeetingId == meetingId, ct);

    public async Task AddFollowUpRecipientsAsync(
        IEnumerable<FollowUpEmailRecipient> recipients, CancellationToken ct) =>
        await _dbContext.FollowUpEmailRecipients.AddRangeAsync(recipients, ct);

    public async Task ClearAnalysisAsync(Guid meetingId, CancellationToken ct)
    {
        var summaries = await _dbContext.MeetingSummaries.Where(s => s.MeetingId == meetingId).ToListAsync(ct);
        var actionItems = await _dbContext.ActionItems.Where(a => a.MeetingId == meetingId).ToListAsync(ct);
        var decisions = await _dbContext.Decisions.Where(d => d.MeetingId == meetingId).ToListAsync(ct);
        var emails = await _dbContext.FollowUpEmails.Where(e => e.MeetingId == meetingId).ToListAsync(ct);

        _dbContext.MeetingSummaries.RemoveRange(summaries);
        _dbContext.ActionItems.RemoveRange(actionItems);
        _dbContext.Decisions.RemoveRange(decisions);
        _dbContext.FollowUpEmails.RemoveRange(emails);
    }

    public async Task AddAnalysisAsync(
        MeetingSummary summary,
        IEnumerable<ActionItem> actionItems,
        IEnumerable<Decision> decisions,
        FollowUpEmail followUpEmail,
        CancellationToken ct)
    {
        await _dbContext.MeetingSummaries.AddAsync(summary, ct);
        await _dbContext.ActionItems.AddRangeAsync(actionItems, ct);
        await _dbContext.Decisions.AddRangeAsync(decisions, ct);
        await _dbContext.FollowUpEmails.AddAsync(followUpEmail, ct);
    }

    public Task SaveChangesAsync(CancellationToken ct) => _dbContext.SaveChangesAsync(ct);
}
