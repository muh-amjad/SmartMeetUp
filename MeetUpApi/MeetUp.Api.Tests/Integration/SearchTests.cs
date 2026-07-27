using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Jobs;
using MeetUp.Api.Services.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Phase 7 search tests. These cover the keyword arm and — most importantly — the access scoping,
/// since search is the one feature that reads across every meeting in the database. The semantic arm
/// needs a live embedding API key and so is not exercised here.
/// </summary>
public class SearchTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SearchTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Finds_A_Meeting_By_A_Word_From_Its_Transcript()
    {
        var host = await CreateUser("search-host");
        var meetingId = await CreateMeeting(host.Token, "Pricing discussion");

        await IndexTranscriptAsync(meetingId, host.UserId,
            "We reviewed the enterprise pricing tiers and agreed the discount stays at fifteen percent.");

        var results = await SearchAsync(host.Token, "pricing");

        var hit = Assert.Single(results);
        Assert.Equal(meetingId, hit.MeetingId);
        Assert.Equal("Pricing discussion", hit.MeetingTitle);
        Assert.Contains("pricing", hit.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Never_Returns_Chunks_From_Meetings_The_Caller_Was_Not_Part_Of()
    {
        var owner = await CreateUser("search-owner");
        var stranger = await CreateUser("search-stranger");

        var privateMeetingId = await CreateMeeting(owner.Token, "Confidential board call");
        await IndexTranscriptAsync(privateMeetingId, owner.UserId,
            "The acquisition of Northwind closes in October at a valuation of forty million.");

        // The owner can find it...
        var ownerResults = await SearchAsync(owner.Token, "acquisition");
        Assert.Contains(ownerResults, r => r.MeetingId == privateMeetingId);

        // ...the stranger must not, even though the row exists and matches the query.
        var strangerResults = await SearchAsync(stranger.Token, "acquisition");
        Assert.DoesNotContain(strangerResults, r => r.MeetingId == privateMeetingId);
    }

    [Fact]
    public async Task Finds_Meetings_The_Caller_Attended_But_Did_Not_Host()
    {
        var host = await CreateUser("search-attend-host");
        var attendee = await CreateUser("search-attendee");

        var meetingId = await CreateMeeting(host.Token, "Sprint planning");
        await IndexTranscriptAsync(meetingId, host.UserId,
            "Kubernetes migration is deferred until the observability work lands.");

        // Attendee joins as a participant.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.MeetingParticipants.Add(new MeetingParticipant
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                UserId = attendee.UserId,
                Role = ParticipantRole.Participant,
                JoinedUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var results = await SearchAsync(attendee.Token, "kubernetes");
        Assert.Contains(results, r => r.MeetingId == meetingId);
    }

    [Fact]
    public async Task Returns_A_Timestamp_So_Results_Can_Jump_Into_The_Recording()
    {
        var host = await CreateUser("search-seek");
        var meetingId = await CreateMeeting(host.Token, "Timestamped");

        // Two utterances; the matching phrase sits in the second one.
        await IndexTranscriptAsync(meetingId, host.UserId,
            firstUtterance: "Opening remarks and introductions.",
            secondUtterance: "Then we settled the warehouse logistics contract.");

        var results = await SearchAsync(host.Token, "warehouse");

        var hit = Assert.Single(results);
        Assert.True(hit.StartMs >= 0, "a start offset must be present for jump-to-moment");
    }

    [Fact]
    public async Task Rejects_An_Empty_Query_And_An_Unknown_Mode()
    {
        var host = await CreateUser("search-validation");

        var emptyQuery = await SendAuthed(HttpMethod.Get, "/api/search?q=", host.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, emptyQuery.StatusCode);

        var badMode = await SendAuthed(HttpMethod.Get, "/api/search?q=hello&mode=telepathy", host.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badMode.StatusCode);
    }

    [Fact]
    public async Task Requires_Authentication()
    {
        var response = await _client.GetAsync("/api/search?q=anything");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Chunker_Keeps_Timestamps_And_Overlaps_Consecutive_Utterances()
    {
        // Utterances long enough that several chunks are produced.
        var utterances = Enumerable.Range(0, 8)
            .Select(i => new TranscriptUtterance
            {
                Id = Guid.NewGuid(),
                SpeakerLabel = i % 2 == 0 ? "A" : "B",
                StartMs = i * 10_000,
                EndMs = (i + 1) * 10_000,
                Text = new string('x', 400) + $" marker{i}",
            })
            .ToList();

        var chunks = TranscriptChunker.Chunk(utterances);

        Assert.True(chunks.Count > 1, "long transcripts must split into multiple chunks");

        // Timestamps must be real and ordered, not synthesised.
        Assert.Equal(0, chunks[0].StartMs);
        Assert.Equal(80_000, chunks[^1].EndMs);
        foreach (var chunk in chunks)
        {
            Assert.True(chunk.EndMs > chunk.StartMs);
        }

        // Consecutive chunks must overlap in time, otherwise a phrase on the boundary is lost.
        Assert.True(chunks[1].StartMs < chunks[0].EndMs, "chunks should overlap");

        // Every utterance must appear somewhere.
        var combined = string.Join(" ", chunks.Select(c => c.Text));
        for (var i = 0; i < utterances.Count; i++)
        {
            Assert.Contains($"marker{i}", combined);
        }
    }

    [Fact]
    public void Chunker_Does_Not_Loop_On_A_Single_Oversized_Utterance()
    {
        var utterances = new List<TranscriptUtterance>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SpeakerLabel = "A",
                StartMs = 0,
                EndMs = 60_000,
                // Far larger than the chunk target on its own.
                Text = new string('y', TranscriptChunker.TargetChars * 3),
            },
        };

        var chunks = TranscriptChunker.Chunk(utterances);

        Assert.Single(chunks);
        Assert.Equal(0, chunks[0].StartMs);
        Assert.Equal(60_000, chunks[0].EndMs);
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    /// <summary>
    /// Seeds a transcript and runs the real EmbeddingJob so chunks (and their generated tsvector)
    /// are produced by production code. No embedding key is configured in tests, so the job stores
    /// chunks without vectors — exactly the keyword-only path this suite exercises.
    /// </summary>
    private async Task IndexTranscriptAsync(
        Guid meetingId, string hostUserId, string firstUtterance, string? secondUtterance = null)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var transcript = new Transcript
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                Language = "en",
                AssemblyAiTranscriptId = "search-test",
                CreatedUtc = DateTime.UtcNow,
                FullText = secondUtterance is null
                    ? firstUtterance
                    : $"{firstUtterance} {secondUtterance}",
            };
            db.Transcripts.Add(transcript);

            db.TranscriptUtterances.Add(new TranscriptUtterance
            {
                Id = Guid.NewGuid(),
                TranscriptId = transcript.Id,
                SpeakerLabel = "A",
                StartMs = 0,
                EndMs = 10_000,
                Text = firstUtterance,
                Confidence = 0.95f,
            });

            if (secondUtterance is not null)
            {
                db.TranscriptUtterances.Add(new TranscriptUtterance
                {
                    Id = Guid.NewGuid(),
                    TranscriptId = transcript.Id,
                    SpeakerLabel = "B",
                    StartMs = 10_000,
                    EndMs = 20_000,
                    Text = secondUtterance,
                    Confidence = 0.95f,
                });
            }

            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<IEmbeddingJob>();
            await job.RunAsync(meetingId, CancellationToken.None);
        }
    }

    private async Task<List<SearchResultDto>> SearchAsync(string token, string query, string mode = "hybrid")
    {
        var response = await SendAuthed(
            HttpMethod.Get, $"/api/search?q={Uri.EscapeDataString(query)}&mode={mode}", token);
        // Surface the ProblemDetails body on failure — a bare status code makes a broken query
        // translation almost impossible to diagnose from test output alone.
        Assert.True(
            response.IsSuccessStatusCode,
            $"search failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<List<SearchResultDto>>() ?? new();
    }

    private async Task<HttpResponseMessage> SendAuthed(HttpMethod method, string url, string bearerToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await _client.SendAsync(request);
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
        var username = $"{prefix}-{suffix}";

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = $"{username}@meetup.test",
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.UserName == username).Select(u => u.Id).SingleAsync();

        return (userId, auth!.Token);
    }
}
