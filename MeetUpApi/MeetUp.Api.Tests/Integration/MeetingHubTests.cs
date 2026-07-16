using MeetUp.Api.Dtos.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

public class MeetingHubTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public MeetingHubTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task JoinUser_Broadcasts_User_List_To_All_Connected_Clients()
    {
        var user1 = await CreateUser("presence-a");
        var user2 = await CreateUser("presence-b");

        var user2UsersReceived = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var hub1 = BuildHubConnection(user1.Token);
        await using var hub2 = BuildHubConnection(user2.Token);

        hub2.On<JsonElement>("UserJoined", payload =>
        {
            // Wait until we can see both users in the presence list
            var users = payload.EnumerateArray().ToList();
            if (users.Count >= 2)
            {
                user2UsersReceived.TrySetResult(payload.Clone());
            }
        });

        await StartAndJoin(hub1);
        await StartAndJoin(hub2);

        var payload = await AwaitWithTimeout(user2UsersReceived.Task, "hub2 did not see hub1 in the presence list.");
        var usernames = payload.EnumerateArray()
            .Select(u => u.GetProperty("username").GetString())
            .ToList();
        Assert.Contains(user1.Username, usernames);
        Assert.Contains(user2.Username, usernames);
    }

    [Fact]
    public async Task InviteToMeeting_Delivers_ReceiveInvite_To_Callee()
    {
        var caller = await CreateUser("caller-invite");
        var callee = await CreateUser("callee-invite");

        var calleeIncoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => calleeIncoming.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);

        var invite = await AwaitWithTimeout(calleeIncoming.Task, "Callee did not receive invite.");
        Assert.Equal(caller.Username, invite.GetProperty("fromUsername").GetString());
        Assert.Equal(meetingId, invite.GetProperty("meetingId").GetString(),
            ignoreCase: true);
    }

    [Fact]
    public async Task Declining_Invite_Notifies_Caller()
    {
        var caller = await CreateUser("caller-decline");
        var callee = await CreateUser("callee-decline");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var declined = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        callerHub.On<JsonElement>("InviteDeclined", payload => declined.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);

        var invite = await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");
        var inviteId = invite.GetProperty("inviteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(inviteId));

        await calleeHub.InvokeAsync("RespondToInvite", inviteId!, false);

        var payload = await AwaitWithTimeout(declined.Task, "Caller did not receive InviteDeclined.");
        Assert.Equal(callee.Username, payload.GetProperty("declinedByUsername").GetString());
    }

    [Fact]
    public async Task Accepting_Invite_Notifies_Both_Parties()
    {
        var caller = await CreateUser("caller-accept");
        var callee = await CreateUser("callee-accept");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerAccepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calleeAccepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("InviteAccepted", payload => callerAccepted.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("InviteAccepted", payload => calleeAccepted.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        var invite = await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");

        await calleeHub.InvokeAsync("RespondToInvite", invite.GetProperty("inviteId").GetString()!, true);

        var callerPayload = await AwaitWithTimeout(callerAccepted.Task, "Caller did not receive InviteAccepted.");
        var calleePayload = await AwaitWithTimeout(calleeAccepted.Task, "Callee did not receive InviteAccepted.");

        // Both sides get the same meetingId back
        Assert.Equal(meetingId, callerPayload.GetProperty("meetingId").GetString(), ignoreCase: true);
        Assert.Equal(meetingId, calleePayload.GetProperty("meetingId").GetString(), ignoreCase: true);
        Assert.Equal(callee.Username, callerPayload.GetProperty("acceptedByUsername").GetString());
    }

    [Fact]
    public async Task SetInCall_Then_SetLeftCall_Broadcast_Updated_Presence()
    {
        var observer = await CreateUser("observer");
        var active = await CreateUser("active-user");

        await using var observerHub = BuildHubConnection(observer.Token);
        await using var activeHub = BuildHubConnection(active.Token);

        var sawInCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawAvailable = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        observerHub.On<JsonElement>("UserJoined", payload =>
        {
            var activeEntry = payload.EnumerateArray()
                .FirstOrDefault(u => string.Equals(
                    u.GetProperty("username").GetString(), active.Username, StringComparison.Ordinal));

            if (activeEntry.ValueKind == JsonValueKind.Object)
            {
                var isInCall = activeEntry.GetProperty("isInCall").GetBoolean();
                if (isInCall) sawInCall.TrySetResult(payload.Clone());
                else if (sawInCall.Task.IsCompleted) sawAvailable.TrySetResult(payload.Clone());
            }
        });

        await StartAndJoin(observerHub);
        await StartAndJoin(activeHub);

        var meetingId = await CreateMeeting(active.Token);
        await activeHub.InvokeAsync("SetInCall", meetingId);
        await AwaitWithTimeout(sawInCall.Task, "Observer did not see active-user as InCall.");

        await activeHub.InvokeAsync("SetLeftCall");
        await AwaitWithTimeout(sawAvailable.Task, "Observer did not see active-user return to Available.");
    }

    [Fact]
    public async Task MeetingsApi_Create_Returns_LiveKit_Token()
    {
        var host = await CreateUser("meet-host");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title = "Smoke test" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("meetingId").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("livekitToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("livekitWsUrl").GetString()));
        Assert.StartsWith("meeting-", root.GetProperty("roomName").GetString());
    }

    // ────────────── helpers ──────────────

    private async Task<string> CreateMeeting(string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title = "Test meeting" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("meetingId").GetString()!;
    }

    private async Task StartAndJoin(HubConnection connection)
    {
        await connection.StartAsync();
        await connection.InvokeAsync("JoinUser");
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

        var auth = await SignupAndLogin(username, email);
        return (username, email, auth.Token);
    }

    private async Task<AuthResponseDto> SignupAndLogin(string username, string email)
    {
        var signupResponse = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = email,
            Password = "Password123!",
        });

        signupResponse.EnsureSuccessStatusCode();
        var signupAuth = await signupResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return signupAuth!;
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