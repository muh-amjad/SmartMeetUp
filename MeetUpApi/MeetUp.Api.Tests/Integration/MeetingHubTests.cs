using MeetUp.Api.Data;
using MeetUp.Api.Dtos;
using MeetUp.Api.Dtos.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task ReportSpeakingIntervals_Is_Stored_For_A_Participant_Even_When_Presence_Was_Lost()
    {
        var host = await CreateUser("intervals-host");
        var meetingId = await CreateMeeting(host.Token);

        // No SetInCall: this is the state a reconnect or an API restart leaves a connection in —
        // registered, but in no meeting as far as presence knows. Intervals reported then used to
        // be dropped, and the speaker's transcript lines stayed "Speaker B".
        await using var hub = BuildHubConnection(host.Token);
        await StartAndJoin(hub);

        var started = DateTime.UtcNow.AddSeconds(1);
        await hub.InvokeAsync("ReportSpeakingIntervals", Guid.Parse(meetingId), new[]
        {
            new SpeakingIntervalDto { StartedUtc = started, StoppedUtc = started.AddSeconds(2) },
        });

        Assert.Equal(1, await CountSpeakingIntervals(meetingId));
    }

    [Fact]
    public async Task ReportSpeakingIntervals_Is_Ignored_For_Someone_Who_Is_Not_A_Participant()
    {
        var host = await CreateUser("intervals-owner");
        var outsider = await CreateUser("intervals-outsider");
        var meetingId = await CreateMeeting(host.Token);

        await using var hub = BuildHubConnection(outsider.Token);
        await StartAndJoin(hub);

        var started = DateTime.UtcNow.AddSeconds(1);
        await hub.InvokeAsync("ReportSpeakingIntervals", Guid.Parse(meetingId), new[]
        {
            new SpeakingIntervalDto { StartedUtc = started, StoppedUtc = started.AddSeconds(2) },
        });

        Assert.Equal(0, await CountSpeakingIntervals(meetingId));
    }

    [Fact]
    public async Task InviteToMeeting_Is_Refused_When_The_Caller_Is_Not_In_That_Meeting()
    {
        var host = await CreateUser("invite-owner");
        var outsider = await CreateUser("invite-outsider");
        var callee = await CreateUser("invite-target");
        var meetingId = await CreateMeeting(host.Token);

        var calleeIncoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outsiderFailed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var outsiderHub = BuildHubConnection(outsider.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => calleeIncoming.TrySetResult(payload.Clone()));
        outsiderHub.On<string>("CallFailed", message => outsiderFailed.TrySetResult(message));

        await StartAndJoin(outsiderHub);
        await StartAndJoin(calleeHub);

        await outsiderHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);

        var message = await AwaitWithTimeout(outsiderFailed.Task, "Outsider was not told the invite failed.");
        Assert.Contains("not part of this meeting", message);

        var completed = await Task.WhenAny(calleeIncoming.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(calleeIncoming.Task, completed);
    }

    [Fact]
    public async Task Caller_Hanging_Up_Stops_The_Callee_Ringing()
    {
        var caller = await CreateUser("cancel-caller");
        var callee = await CreateUser("cancel-callee");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("InviteCancelled", payload => cancelled.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("SetInCall", meetingId);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        var invite = await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");

        await callerHub.InvokeAsync("SetLeftCall");

        var payload = await AwaitWithTimeout(cancelled.Task, "Callee was not told the call was withdrawn.");
        Assert.Equal(invite.GetProperty("inviteId").GetString(), payload.GetProperty("inviteId").GetString());

        // Answering a withdrawn call is refused, so the callee doesn't join an empty room.
        var stillValid = await calleeHub.InvokeAsync<bool>(
            "RespondToInvite", invite.GetProperty("inviteId").GetString()!, true);
        Assert.False(stillValid);
    }

    [Fact]
    public async Task Caller_Disconnecting_Stops_The_Callee_Ringing()
    {
        var caller = await CreateUser("drop-caller");
        var callee = await CreateUser("drop-callee");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("InviteCancelled", payload => cancelled.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");

        await callerHub.DisposeAsync();

        await AwaitWithTimeout(cancelled.Task, "Callee kept ringing after the caller disconnected.");
    }

    [Fact]
    public async Task Unanswered_Invite_Is_Reported_To_The_Caller_As_Missed()
    {
        var caller = await CreateUser("miss-caller");
        var callee = await CreateUser("miss-callee");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var missed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("InviteMissed", payload => missed.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        var invite = await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");

        await calleeHub.InvokeAsync("MissInvite", invite.GetProperty("inviteId").GetString()!);

        var payload = await AwaitWithTimeout(missed.Task, "Caller was not told the call was missed.");
        Assert.Equal(callee.Username, payload.GetProperty("missedByUsername").GetString());
        Assert.Equal(calleeHub.ConnectionId, payload.GetProperty("missedByUserId").GetString());
    }

    [Fact]
    public async Task Callee_Disconnecting_While_Ringing_Is_Reported_As_Missed()
    {
        var caller = await CreateUser("gone-caller");
        var callee = await CreateUser("gone-callee");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var missed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("InviteMissed", payload => missed.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        await AwaitWithTimeout(incoming.Task, "Callee did not receive invite.");

        await calleeHub.DisposeAsync();

        var payload = await AwaitWithTimeout(missed.Task, "Caller kept waiting on someone who disconnected.");
        Assert.Equal(callee.Username, payload.GetProperty("missedByUsername").GetString());
    }

    [Fact]
    public async Task Only_The_Callee_Can_Answer_An_Invite()
    {
        var caller = await CreateUser("answer-caller");
        var callee = await CreateUser("answer-callee");
        var bystander = await CreateUser("answer-bystander");

        var incoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);
        await using var bystanderHub = BuildHubConnection(bystander.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => incoming.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);
        await StartAndJoin(bystanderHub);

        var meetingId = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, meetingId);
        var inviteId = (await AwaitWithTimeout(incoming.Task, "Callee did not receive invite."))
            .GetProperty("inviteId").GetString()!;

        Assert.False(await bystanderHub.InvokeAsync<bool>("RespondToInvite", inviteId, false));

        // The bystander's attempt must not have used up the invite.
        Assert.True(await calleeHub.InvokeAsync<bool>("RespondToInvite", inviteId, true));
    }

    [Fact]
    public async Task Calling_Into_Another_Meeting_From_Inside_A_Call_Is_Refused()
    {
        var caller = await CreateUser("busy-caller");
        var callee = await CreateUser("busy-callee");

        var calleeIncoming = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerFailed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveInvite", payload => calleeIncoming.TrySetResult(payload.Clone()));
        callerHub.On<string>("CallFailed", message => callerFailed.TrySetResult(message));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        var currentMeeting = await CreateMeeting(caller.Token);
        var otherMeeting = await CreateMeeting(caller.Token);
        await callerHub.InvokeAsync("SetInCall", currentMeeting);

        await callerHub.InvokeAsync("InviteToMeeting", calleeHub.ConnectionId, otherMeeting);

        var message = await AwaitWithTimeout(callerFailed.Task, "Caller was not told to leave their call first.");
        Assert.Contains("Leave your current call", message);

        var completed = await Task.WhenAny(calleeIncoming.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert.NotSame(calleeIncoming.Task, completed);
    }

    // ────────────── helpers ──────────────

    private async Task<int> CountSpeakingIntervals(string meetingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var id = Guid.Parse(meetingId);
        return await db.ParticipantAudioActivities.CountAsync(a => a.MeetingId == id);
    }

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