using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace MeetUp.Api.Repositories;

public sealed class MeetingAnalyticsRepository : IMeetingAnalyticsRepository
{
    private readonly AppDbContext _dbContext;

    public MeetingAnalyticsRepository(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAudioActivityAsync(IEnumerable<ParticipantAudioActivity> intervals, CancellationToken ct)
    {
        await _dbContext.ParticipantAudioActivities.AddRangeAsync(intervals, ct);
    }

    public async Task<IReadOnlyList<ParticipantAudioActivity>> GetAudioActivityAsync(Guid meetingId, CancellationToken ct) =>
        await _dbContext.ParticipantAudioActivities
            .Where(a => a.MeetingId == meetingId)
            .OrderBy(a => a.StartedSpeakingMs)
            .ToListAsync(ct);

    public Task<MeetingAnalytics?> GetAnalyticsAsync(Guid meetingId, CancellationToken ct) =>
        _dbContext.MeetingAnalytics.FirstOrDefaultAsync(a => a.MeetingId == meetingId, ct);

    public async Task UpsertAnalyticsAsync(MeetingAnalytics analytics, CancellationToken ct)
    {
        var existing = await _dbContext.MeetingAnalytics
            .FirstOrDefaultAsync(a => a.MeetingId == analytics.MeetingId, ct);

        if (existing is null)
        {
            await _dbContext.MeetingAnalytics.AddAsync(analytics, ct);
            return;
        }

        existing.TotalDurationSeconds = analytics.TotalDurationSeconds;
        existing.ParticipantCount = analytics.ParticipantCount;
        existing.SpeakingDistribution = analytics.SpeakingDistribution;
        existing.WordCount = analytics.WordCount;
        existing.AverageWordsPerMinute = analytics.AverageWordsPerMinute;
        existing.ComputedUtc = analytics.ComputedUtc;
    }

    public async Task<IReadOnlyList<MeetingAnalytics>> GetAnalyticsForUserAsync(
        string userId, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        await _dbContext.MeetingAnalytics
            .Include(a => a.Meeting)
            .Where(a =>
                a.Meeting!.CreatedUtc >= fromUtc &&
                a.Meeting.CreatedUtc <= toUtc &&
                (a.Meeting.HostUserId == userId ||
                 a.Meeting.Participants.Any(p => p.UserId == userId)))
            .OrderBy(a => a.Meeting!.CreatedUtc)
            .ToListAsync(ct);

    public Task SaveChangesAsync(CancellationToken ct) => _dbContext.SaveChangesAsync(ct);
}
