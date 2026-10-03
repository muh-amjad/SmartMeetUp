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

public class StaleMeetingSweepTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public StaleMeetingSweepTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Fails_A_Meeting_Stuck_In_Processing_So_It_Can_Be_Opened_And_Retried()
    {
        var stuck = await CreateMeetingAsync("sweep-stuck");
        var working = await CreateMeetingAsync("sweep-working");
        var ready = await CreateMeetingAsync("sweep-ready");

        await SetAsync(stuck, MeetingStatus.Processing, endedAgo: TimeSpan.FromHours(3));
        await SetAsync(working, MeetingStatus.Processing, endedAgo: TimeSpan.FromMinutes(20));
        await SetAsync(ready, MeetingStatus.Ready, endedAgo: TimeSpan.FromHours(5));

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IStaleMeetingSweepJob>().RunAsync(CancellationToken.None);
        }

        Assert.Equal(MeetingStatus.Failed, await StatusAsync(stuck));
        Assert.Equal(MeetingStatus.Processing, await StatusAsync(working));   // still within its window
        Assert.Equal(MeetingStatus.Ready, await StatusAsync(ready));          // finished meetings untouched
    }

    private async Task SetAsync(Guid meetingId, MeetingStatus status, TimeSpan endedAgo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meeting = await db.Meetings.SingleAsync(m => m.Id == meetingId);
        meeting.Status = status;
        meeting.EndedUtc = DateTime.UtcNow - endedAgo;
        await db.SaveChangesAsync();
    }

    private async Task<MeetingStatus> StatusAsync(Guid meetingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Meetings.Where(m => m.Id == meetingId).Select(m => m.Status).SingleAsync();
    }

    private async Task<Guid> CreateMeetingAsync(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var signup = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = $"{prefix}-{suffix}",
            Email = $"{prefix}-{suffix}@meetup.test",
            Password = "Password123!",
        });
        signup.EnsureSuccessStatusCode();
        var auth = await signup.Content.ReadFromJsonAsync<AuthResponseDto>();

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title = "Sweep test" }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        var response = await _client.SendAsync(create);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return Guid.Parse(doc.RootElement.GetProperty("meetingId").GetString()!);
    }
}
