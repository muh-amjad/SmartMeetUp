using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Dtos.Meetings;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Integration tests for the Phase 2 endpoints on <c>MeetingsController</c>
/// (list / detail / patch / delete / chat history) and the
/// <c>MeetingHub.SendChatMessage</c> real-time chat flow.
/// </summary>
public class MeetingsApiTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MeetingsApiTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ─────────────────────────────  REST endpoints  ─────────────────────────────

    [Fact]
    public async Task ListMeetings_Returns_Only_Callers_Own_Meetings()
    {
        var host = await CreateUser("list-host");
        var stranger = await CreateUser("list-stranger");

        var hostMeetingId = await CreateMeeting(host.Token, "Host meeting");
        var strangerMeetingId = await CreateMeeting(stranger.Token, "Stranger meeting");

        var response = await SendAuthed(HttpMethod.Get, "/api/meetings", host.Token);
        response.EnsureSuccessStatusCode();

        var items = await response.Content.ReadFromJsonAsync<List<MeetingListItemDto>>();
        Assert.NotNull(items);

        Assert.Contains(items!, m => m.MeetingId == hostMeetingId);
        Assert.DoesNotContain(items!, m => m.MeetingId == strangerMeetingId);

        var hostRow = items!.Single(m => m.MeetingId == hostMeetingId);
        Assert.True(hostRow.IsHost);
        Assert.Equal("Host meeting", hostRow.Title);
    }

    [Fact]
    public async Task GetMeeting_Returns_Detail_For_Host()
    {
        var host = await CreateUser("detail-host");
        var meetingId = await CreateMeeting(host.Token, "Detail me");

        var response = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}", host.Token);
        response.EnsureSuccessStatusCode();

        var detail = await response.Content.ReadFromJsonAsync<MeetingDetailDto>();
        Assert.NotNull(detail);
        Assert.Equal(meetingId, detail!.MeetingId);
        Assert.Equal("Detail me", detail.Title);
        Assert.True(detail.IsHost);
        Assert.Equal(host.Username, detail.HostUsername);
        Assert.Empty(detail.Participants);   // no participant_joined webhook fired
    }

    [Fact]
    public async Task GetMeeting_Returns_403_For_Non_Participant()
    {
        var host = await CreateUser("detail-owner");
        var stranger = await CreateUser("detail-stranger");
        var meetingId = await CreateMeeting(host.Token, "Private");

        var response = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}", stranger.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetMeeting_Returns_404_For_Missing_Id()
    {
        var host = await CreateUser("detail-404");
        var response = await SendAuthed(HttpMethod.Get, $"/api/meetings/{Guid.NewGuid()}", host.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task UpdateMeeting_As_Host_Changes_Title()
    {
        var host = await CreateUser("patch-host");
        var meetingId = await CreateMeeting(host.Token, "Old title");

        var patch = await SendAuthed(HttpMethod.Patch, $"/api/meetings/{meetingId}", host.Token,
            new UpdateMeetingRequestDto { Title = "New shiny title" });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var detailResp = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}", host.Token);
        var detail = await detailResp.Content.ReadFromJsonAsync<MeetingDetailDto>();
        Assert.Equal("New shiny title", detail!.Title);
    }

    [Fact]
    public async Task UpdateMeeting_As_Non_Host_Returns_403()
    {
        var host = await CreateUser("patch-owner");
        var stranger = await CreateUser("patch-stranger");
        var meetingId = await CreateMeeting(host.Token, "No touchy");

        var response = await SendAuthed(HttpMethod.Patch, $"/api/meetings/{meetingId}", stranger.Token,
            new UpdateMeetingRequestDto { Title = "Hacked" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteMeeting_As_Host_Removes_The_Meeting()
    {
        var host = await CreateUser("del-host");
        var meetingId = await CreateMeeting(host.Token, "Bye");

        var del = await SendAuthed(HttpMethod.Delete, $"/api/meetings/{meetingId}", host.Token);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var afterGet = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}", host.Token);
        Assert.Equal(HttpStatusCode.NotFound, afterGet.StatusCode);
    }

    [Fact]
    public async Task DeleteMeeting_As_Non_Host_Returns_403()
    {
        var host = await CreateUser("del-owner");
        var stranger = await CreateUser("del-stranger");
        var meetingId = await CreateMeeting(host.Token, "Keep me");

        var response = await SendAuthed(HttpMethod.Delete, $"/api/meetings/{meetingId}", stranger.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetChat_Returns_Empty_List_For_New_Meeting()
    {
        var host = await CreateUser("chat-empty");
        var meetingId = await CreateMeeting(host.Token, "Silent");

        var response = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}/chat", host.Token);
        response.EnsureSuccessStatusCode();

        var messages = await response.Content.ReadFromJsonAsync<List<ChatMessageDto>>();
        Assert.NotNull(messages);
        Assert.Empty(messages!);
    }

    [Fact]
    public async Task GetChat_Returns_403_For_Non_Participant()
    {
        var host = await CreateUser("chat-owner");
        var stranger = await CreateUser("chat-stranger");
        var meetingId = await CreateMeeting(host.Token, "Private chat");

        var response = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}/chat", stranger.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ─────────────────────────────  Hub SendChatMessage  ─────────────────────────────

    [Fact]
    public async Task SendChatMessage_Persists_And_Broadcasts_To_Meeting_Group()
    {
        var host = await CreateUser("chat-sender");
        var meetingId = await CreateMeeting(host.Token, "Chat room");

        var chatReceived = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hub = BuildHubConnection(host.Token);
        hub.On<JsonElement>("ChatMessageReceived", payload => chatReceived.TrySetResult(payload.Clone()));

        await hub.StartAsync();
        await hub.InvokeAsync("JoinUser");
        // SetInCall places the caller in the SignalR group "meeting-{guid:N}"
        await hub.InvokeAsync("SetInCall", meetingId);

        await hub.InvokeAsync("SendChatMessage", meetingId, "hello world");

        var payload = await AwaitWithTimeout(chatReceived.Task, "ChatMessageReceived not fired.");
        Assert.Equal("hello world", payload.GetProperty("text").GetString());
        Assert.Equal(host.Username, payload.GetProperty("senderUsername").GetString());

        // And it must be persisted — GET /chat should now return it
        var historyResp = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}/chat", host.Token);
        var history = await historyResp.Content.ReadFromJsonAsync<List<ChatMessageDto>>();
        Assert.Single(history!);
        Assert.Equal("hello world", history![0].Text);
    }

    [Fact]
    public async Task SendChatMessage_Ignored_When_User_Not_In_Meeting()
    {
        var host = await CreateUser("chat-outsider");
        var meetingId = await CreateMeeting(host.Token, "No entry");

        await using var hub = BuildHubConnection(host.Token);
        var chatReceived = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<JsonElement>("ChatMessageReceived", payload => chatReceived.TrySetResult(payload.Clone()));

        await hub.StartAsync();
        await hub.InvokeAsync("JoinUser");
        // NOTE: no SetInCall — user is not in the meeting group
        await hub.InvokeAsync("SendChatMessage", meetingId, "should not go through");

        // Give the hub a moment; nothing should arrive
        var completed = await Task.WhenAny(chatReceived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.NotSame(chatReceived.Task, completed);

        var historyResp = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}/chat", host.Token);
        var history = await historyResp.Content.ReadFromJsonAsync<List<ChatMessageDto>>();
        Assert.Empty(history!);
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    private async Task<Guid> CreateMeeting(string bearerToken, string title = "Test meeting")
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

    private static async Task<T> AwaitWithTimeout<T>(Task<T> task, string timeoutMessage, int timeoutSeconds = 10)
    {
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        Assert.True(completed == task, timeoutMessage);
        return await task;
    }

    private async Task<(string Username, string Email, string Token)> CreateUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}-{suffix}";
        var email = $"{prefix}-{suffix}@meetup.test";

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = email,
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        return (username, email, auth!.Token);
    }

    private HubConnection BuildHubConnection(string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/meetingHub"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(accessToken)!;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }
}
