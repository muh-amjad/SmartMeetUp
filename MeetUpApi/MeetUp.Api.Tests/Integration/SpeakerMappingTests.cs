using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Entities;
using MeetUp.Api.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Tests the Phase 6 speaker-attribution algorithm: overlapping each transcript speaker label with
/// the speaking intervals reported per user, then computing meeting analytics from the result.
/// Runs the job directly against a seeded database rather than through HTTP, since the job is
/// normally triggered by Hangfire.
/// </summary>
public class SpeakerMappingTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SpeakerMappingTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Maps_Each_Speaker_Label_To_The_User_Whose_Speaking_Overlaps_Most()
    {
        var alice = await CreateUser("map-alice");
        var bob = await CreateUser("map-bob");
        var meetingId = await CreateMeeting(alice.Token, "Speaker mapping");

        // Alice speaks 0-5s and 10-15s; Bob speaks 5-10s.
        await SeedAsync(meetingId, seed =>
        {
            seed.AddParticipant(alice.UserId);
            seed.AddParticipant(bob.UserId);

            seed.AddUtterance("A", 0, 5_000, "We should ship the billing page.");
            seed.AddUtterance("B", 5_000, 10_000, "I will handle the QA pass.");
            seed.AddUtterance("A", 10_000, 15_000, "Great, let us aim for Friday.");

            seed.AddActivity(alice.UserId, 0, 5_000);
            seed.AddActivity(bob.UserId, 5_000, 10_000);
            seed.AddActivity(alice.UserId, 10_000, 15_000);
        });

        await RunJobAsync(meetingId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var utterances = await db.TranscriptUtterances
            .Where(u => u.Transcript!.MeetingId == meetingId)
            .OrderBy(u => u.StartMs)
            .ToListAsync();

        Assert.Equal(alice.UserId, utterances[0].ParticipantUserId);
        Assert.Equal(bob.UserId, utterances[1].ParticipantUserId);
        Assert.Equal(alice.UserId, utterances[2].ParticipantUserId);

        // Alice spoke for 10s across two turns, Bob for 5s.
        var analytics = await db.MeetingAnalytics.SingleAsync(a => a.MeetingId == meetingId);
        Assert.Equal(2, analytics.ParticipantCount);

        var aliceShare = analytics.SpeakingDistribution.Single(s => s.UserId == alice.UserId);
        var bobShare = analytics.SpeakingDistribution.Single(s => s.UserId == bob.UserId);
        Assert.Equal(10, aliceShare.Seconds);
        Assert.Equal(5, bobShare.Seconds);
        Assert.Equal(66.7, aliceShare.Percent);
        Assert.Equal(33.3, bobShare.Percent);

        // Six words in each of the three utterances above.
        Assert.Equal(18, analytics.WordCount);
    }

    [Fact]
    public async Task Never_Assigns_Two_Speaker_Labels_To_The_Same_User()
    {
        var loud = await CreateUser("map-loud");
        var quiet = await CreateUser("map-quiet");
        var meetingId = await CreateMeeting(loud.Token, "Greedy assignment");

        // The loud user's reported activity spans the whole meeting and so overlaps both labels.
        // A naive per-label argmax would hand them both; each label must go to a distinct user.
        await SeedAsync(meetingId, seed =>
        {
            seed.AddParticipant(loud.UserId);
            seed.AddParticipant(quiet.UserId);

            seed.AddUtterance("A", 0, 8_000, "one two three");
            seed.AddUtterance("B", 8_000, 10_000, "four five");

            seed.AddActivity(loud.UserId, 0, 10_000);
            seed.AddActivity(quiet.UserId, 8_000, 10_000);
        });

        await RunJobAsync(meetingId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var utterances = await db.TranscriptUtterances
            .Where(u => u.Transcript!.MeetingId == meetingId)
            .OrderBy(u => u.StartMs)
            .ToListAsync();

        var assigned = utterances.Select(u => u.ParticipantUserId).Distinct().ToList();
        Assert.Equal(2, assigned.Count);
        Assert.Equal(loud.UserId, utterances[0].ParticipantUserId);   // strongest overlap wins first
        Assert.Equal(quiet.UserId, utterances[1].ParticipantUserId);
    }

    [Fact]
    public async Task Leaves_Speakers_Anonymous_But_Still_Reports_Totals_When_No_Activity_Was_Captured()
    {
        var host = await CreateUser("map-noactivity");
        var meetingId = await CreateMeeting(host.Token, "No activity reported");

        await SeedAsync(meetingId, seed =>
        {
            seed.AddParticipant(host.UserId);
            seed.AddUtterance("A", 0, 4_000, "hello there friends");
            // deliberately no AddActivity — e.g. an older meeting recorded before Phase 6
        });

        await RunJobAsync(meetingId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var utterance = await db.TranscriptUtterances
            .SingleAsync(u => u.Transcript!.MeetingId == meetingId);
        Assert.Null(utterance.ParticipantUserId);

        // Analytics must still exist so the UI has something to show.
        var analytics = await db.MeetingAnalytics.SingleAsync(a => a.MeetingId == meetingId);
        Assert.Equal(3, analytics.WordCount);
        Assert.Equal(1, analytics.ParticipantCount);
        Assert.All(analytics.SpeakingDistribution, s => Assert.Equal(0, s.Seconds));
    }

    /// <summary>
    /// Guards the other jsonb-backed collection in the schema. Nothing wrote a MeetingSummary in a
    /// test before, so a serialization failure on KeyTopics would only have surfaced at runtime.
    /// </summary>
    [Fact]
    public async Task Persists_Summary_Key_Topics_To_Jsonb_And_Reads_Them_Back()
    {
        var host = await CreateUser("summary-jsonb");
        var meetingId = await CreateMeeting(host.Token, "Key topics round-trip");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MeetingSummaries.Add(new MeetingSummary
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                OverviewText = "We agreed to ship on Friday.",
                KeyTopics = new List<string> { "billing page", "QA", "release date" },
                ProviderKey = "test-provider",
                ModelUsed = "test-model",
                GeneratedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stored = await db.MeetingSummaries.SingleAsync(s => s.MeetingId == meetingId);
            Assert.Equal(new[] { "billing page", "QA", "release date" }, stored.KeyTopics);
        }
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    private async Task RunJobAsync(Guid meetingId)
    {
        using var scope = _factory.Services.CreateScope();
        var job = scope.ServiceProvider.GetRequiredService<ISpeakerMappingJob>();
        await job.RunAsync(meetingId, CancellationToken.None);
    }

    private async Task SeedAsync(Guid meetingId, Action<Seeder> configure)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var transcript = new Transcript
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            Language = "en",
            AssemblyAiTranscriptId = "test",
            CreatedUtc = DateTime.UtcNow,
        };

        var seeder = new Seeder(meetingId, transcript);
        configure(seeder);

        transcript.FullText = string.Join(' ', seeder.Utterances.Select(u => u.Text));

        db.Transcripts.Add(transcript);
        db.TranscriptUtterances.AddRange(seeder.Utterances);
        db.MeetingParticipants.AddRange(seeder.Participants);
        db.ParticipantAudioActivities.AddRange(seeder.Activities);

        await db.SaveChangesAsync();
    }

    private sealed class Seeder
    {
        private readonly Guid _meetingId;
        private readonly Transcript _transcript;

        public Seeder(Guid meetingId, Transcript transcript)
        {
            _meetingId = meetingId;
            _transcript = transcript;
        }

        public List<TranscriptUtterance> Utterances { get; } = new();
        public List<MeetingParticipant> Participants { get; } = new();
        public List<ParticipantAudioActivity> Activities { get; } = new();

        public void AddUtterance(string label, int startMs, int endMs, string text) =>
            Utterances.Add(new TranscriptUtterance
            {
                Id = Guid.NewGuid(),
                TranscriptId = _transcript.Id,
                SpeakerLabel = label,
                StartMs = startMs,
                EndMs = endMs,
                Text = text,
                Confidence = 0.9f,
            });

        public void AddParticipant(string userId) =>
            Participants.Add(new MeetingParticipant
            {
                Id = Guid.NewGuid(),
                MeetingId = _meetingId,
                UserId = userId,
                Role = ParticipantRole.Participant,
                JoinedUtc = DateTime.UtcNow,
            });

        public void AddActivity(string userId, int startMs, int stopMs) =>
            Activities.Add(new ParticipantAudioActivity
            {
                Id = Guid.NewGuid(),
                MeetingId = _meetingId,
                UserId = userId,
                StartedSpeakingMs = startMs,
                StoppedSpeakingMs = stopMs,
            });
    }

    private async Task<Guid> CreateMeeting(string bearerToken, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("meetingId").GetGuid();
    }

    private async Task<(string UserId, string Token)> CreateUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = $"{prefix}-{suffix}",
            Email = $"{prefix}-{suffix}@meetup.test",
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users
            .Where(u => u.UserName == $"{prefix}-{suffix}")
            .Select(u => u.Id)
            .SingleAsync();

        return (userId, auth!.Token);
    }
}
