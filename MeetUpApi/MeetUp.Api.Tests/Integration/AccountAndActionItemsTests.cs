using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Phase 8 endpoints: the cross-meeting action-items list that the dashboard and action-items page
/// read from, plus account profile and password management.
/// </summary>
public class AccountAndActionItemsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AccountAndActionItemsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Action_Items_Span_Meetings_And_Respect_Each_Filter()
    {
        var user = await CreateUser("ai-filters");
        var meetingA = await CreateMeeting(user.Token, "Planning");
        var meetingB = await CreateMeeting(user.Token, "Retro");

        await SeedActionItemsAsync(
            (meetingA, "Ship the billing page", ActionItemStatus.Open, DateTime.UtcNow.AddDays(-2), user.UserId),
            (meetingA, "Write the changelog", ActionItemStatus.Open, DateTime.UtcNow.AddDays(7), null),
            (meetingB, "Book the venue", ActionItemStatus.Done, null, user.UserId));

        var all = await GetActionItems(user.Token, "all");
        Assert.Equal(3, all.Count);
        // Items come from two different meetings, each labelled with where it came from.
        Assert.Contains(all, i => i.MeetingTitle == "Planning");
        Assert.Contains(all, i => i.MeetingTitle == "Retro");

        var open = await GetActionItems(user.Token, "open");
        Assert.Equal(2, open.Count);
        Assert.All(open, i => Assert.Equal("Open", i.Status));

        var done = await GetActionItems(user.Token, "done");
        Assert.Single(done);
        Assert.Equal("Book the venue", done[0].Description);

        // Overdue = still open and past its due date. The done item is also past due but must not count.
        var overdue = await GetActionItems(user.Token, "overdue");
        Assert.Single(overdue);
        Assert.Equal("Ship the billing page", overdue[0].Description);
        Assert.True(overdue[0].IsOverdue);

        var mine = await GetActionItems(user.Token, "mine");
        Assert.Equal(2, mine.Count);
        Assert.All(mine, i => Assert.True(i.IsAssignedToMe));
    }

    [Fact]
    public async Task Action_Items_Never_Include_Other_Peoples_Meetings()
    {
        var owner = await CreateUser("ai-owner");
        var stranger = await CreateUser("ai-stranger");

        var meetingId = await CreateMeeting(owner.Token, "Private");
        await SeedActionItemsAsync((meetingId, "Secret task", ActionItemStatus.Open, null, owner.UserId));

        Assert.Contains(await GetActionItems(owner.Token, "all"), i => i.Description == "Secret task");
        Assert.DoesNotContain(await GetActionItems(stranger.Token, "all"), i => i.Description == "Secret task");
    }

    [Fact]
    public async Task Rejects_An_Unknown_Action_Item_Filter()
    {
        var user = await CreateUser("ai-badfilter");

        var response = await SendAuthed(HttpMethod.Get, "/api/me/action-items?filter=someday", user.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Display_Name_Can_Be_Read_And_Updated()
    {
        var user = await CreateUser("profile-user");

        var update = await SendAuthed(HttpMethod.Patch, "/api/me/profile", user.Token,
            new UpdateProfileRequestDto { DisplayName = "Amjad Ashfaq" });
        Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);

        var read = await SendAuthed(HttpMethod.Get, "/api/me/profile", user.Token);
        read.EnsureSuccessStatusCode();
        var profile = await read.Content.ReadFromJsonAsync<ProfileDto>();
        Assert.Equal("Amjad Ashfaq", profile!.DisplayName);
    }

    [Fact]
    public async Task Rejects_A_Blank_Display_Name()
    {
        var user = await CreateUser("profile-blank");

        var response = await SendAuthed(HttpMethod.Patch, "/api/me/profile", user.Token,
            new UpdateProfileRequestDto { DisplayName = "   " });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Password_Change_Works_And_The_New_Password_Is_The_One_That_Logs_In()
    {
        var user = await CreateUser("pwd-user");

        var change = await SendAuthed(HttpMethod.Post, "/api/me/password", user.Token,
            new ChangePasswordRequestDto
            {
                CurrentPassword = "Password123!",
                NewPassword = "BrandNewPass456!",
            });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // Old password must stop working, new one must start.
        var oldLogin = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            usernameOrEmail = user.Email,
            password = "Password123!",
        });
        Assert.False(oldLogin.IsSuccessStatusCode);

        var newLogin = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            usernameOrEmail = user.Email,
            password = "BrandNewPass456!",
        });
        newLogin.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Password_Change_Fails_When_The_Current_Password_Is_Wrong()
    {
        var user = await CreateUser("pwd-wrong");

        var response = await SendAuthed(HttpMethod.Post, "/api/me/password", user.Token,
            new ChangePasswordRequestDto
            {
                CurrentPassword = "NotTheRightOne!",
                NewPassword = "BrandNewPass456!",
            });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        // The original password must still work — a failed change must not lock anyone out.
        var login = await _client.PostAsJsonAsync("/api/auth/login", new
        {
            usernameOrEmail = user.Email,
            password = "Password123!",
        });
        login.EnsureSuccessStatusCode();
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    private async Task SeedActionItemsAsync(
        params (Guid MeetingId, string Description, ActionItemStatus Status, DateTime? Due, string? AssigneeId)[] items)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        foreach (var (meetingId, description, status, due, assigneeId) in items)
        {
            db.ActionItems.Add(new ActionItem
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                Description = description,
                Status = status,
                DueDateUtc = due,
                AssigneeUserId = assigneeId,
                CompletedUtc = status == ActionItemStatus.Done ? DateTime.UtcNow : null,
                CreatedUtc = DateTime.UtcNow,
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<List<ActionItemWithMeetingDto>> GetActionItems(string token, string filter)
    {
        var response = await SendAuthed(HttpMethod.Get, $"/api/me/action-items?filter={filter}", token);
        Assert.True(
            response.IsSuccessStatusCode,
            $"action-items failed with {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<List<ActionItemWithMeetingDto>>() ?? new();
    }

    private async Task<HttpResponseMessage> SendAuthed(
        HttpMethod method, string url, string bearerToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
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

    private async Task<(string UserId, string Email, string Token)> CreateUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}-{suffix}";
        var email = $"{username}@meetup.test";

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = email,
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.UserName == username).Select(u => u.Id).SingleAsync();

        return (userId, email, auth!.Token);
    }
}
