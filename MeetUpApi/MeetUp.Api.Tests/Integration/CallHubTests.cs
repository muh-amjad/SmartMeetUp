using MeetUp.Api.Dtos.Auth;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

public class CallHubTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CallHubTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Online_User_Search_And_Call_Initiation_Should_Work()
    {
        var caller = await CreateUser("caller-init");
        var callee = await CreateUser("callee-init");

        var calleeIncomingCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var calleeHub = BuildHubConnection(callee.Token);
        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload =>
        {
            calleeIncomingCall.TrySetResult(payload.Clone());
        });

        await StartAndJoin(calleeHub);

        var searchRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/users/search?query={Uri.EscapeDataString(callee.Username)}");
        searchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", caller.Token);
        var searchResponse = await _client.SendAsync(searchRequest);
        searchResponse.EnsureSuccessStatusCode();

        var searchJson = await searchResponse.Content.ReadAsStringAsync();
        using var searchDocument = JsonDocument.Parse(searchJson);
        var firstResult = searchDocument.RootElement.EnumerateArray().First();
        Assert.True(firstResult.GetProperty("isOnline").GetBoolean());
        var calleeConnectionId = firstResult.GetProperty("connectionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(calleeConnectionId));

        await using var callerHub = BuildHubConnection(caller.Token);
        await StartAndJoin(callerHub);

        await callerHub.InvokeAsync("StartCall", calleeConnectionId!);

        var incomingPayload = await AwaitWithTimeout(calleeIncomingCall.Task, "Callee did not receive incoming call event.");
        Assert.Equal(caller.Username, incomingPayload.GetProperty("fromUsername").GetString());
    }

    [Fact]
    public async Task Declining_Call_Should_Notify_Caller()
    {
        var caller = await CreateUser("caller-decline");
        var callee = await CreateUser("callee-decline");

        var incomingCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callDeclined = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        callerHub.On<JsonElement>("CallDeclined", payload => callDeclined.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingCall.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var incomingPayload = await AwaitWithTimeout(incomingCall.Task, "Callee did not receive incoming call.");

        var inviteId = incomingPayload.GetProperty("inviteId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(inviteId));

        await calleeHub.InvokeAsync("RespondToCall", inviteId!, false);

        var declinedPayload = await AwaitWithTimeout(callDeclined.Task, "Caller did not receive call declined event.");
        Assert.Equal(callee.Username, declinedPayload.GetProperty("declinedByUsername").GetString());
    }

    [Fact]
    public async Task Accepting_Call_Should_Notify_Both_And_Set_User_State()
    {
        var caller = await CreateUser("caller-accept");
        var callee = await CreateUser("callee-accept");

        var incomingCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerAccepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calleeAccepted = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingCall.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("CallAccepted", payload => callerAccepted.TrySetResult(payload.Clone()));
        calleeHub.On<JsonElement>("CallAccepted", payload => calleeAccepted.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var incomingPayload = await AwaitWithTimeout(incomingCall.Task, "Callee did not receive incoming call.");

        await calleeHub.InvokeAsync("RespondToCall", incomingPayload.GetProperty("inviteId").GetString()!, true);

        var callerAcceptedPayload = await AwaitWithTimeout(callerAccepted.Task, "Caller did not receive call accepted event.");
        var calleeAcceptedPayload = await AwaitWithTimeout(calleeAccepted.Task, "Callee did not receive call accepted event.");

        var callerRoom = callerAcceptedPayload.GetProperty("roomId").GetString();
        var calleeRoom = calleeAcceptedPayload.GetProperty("roomId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(callerRoom));
        Assert.Equal(callerRoom, calleeRoom);

        var users = callerAcceptedPayload.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(2, users.Count);

        var calleeInRoom = users.First(user =>
            string.Equals(user.GetProperty("id").GetString(), calleeHub.ConnectionId, StringComparison.Ordinal));

        Assert.True(calleeInRoom.GetProperty("isInCall").GetBoolean());
        Assert.Equal(callerRoom, calleeInRoom.GetProperty("roomId").GetString());
    }

    [Fact]
    public async Task Leaving_Call_Should_Allow_Recalling_Same_User()
    {
        var caller = await CreateUser("caller-recall");
        var callee = await CreateUser("callee-recall");

        var incomingCall1 = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingCall2 = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        var incomingCallCount = 0;
        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload =>
        {
            incomingCallCount++;
            if (incomingCallCount == 1)
            {
                incomingCall1.TrySetResult(payload.Clone());
                return;
            }

            incomingCall2.TrySetResult(payload.Clone());
        });

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var firstIncoming = await AwaitWithTimeout(incomingCall1.Task, "First incoming call was not received.");
        await calleeHub.InvokeAsync("RespondToCall", firstIncoming.GetProperty("inviteId").GetString()!, true);

        await Task.Delay(200);
        await calleeHub.InvokeAsync("LeaveCall");
        await Task.Delay(200);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var secondIncoming = await AwaitWithTimeout(incomingCall2.Task, "Second incoming call was not received after leave.");

        Assert.NotEqual(firstIncoming.GetProperty("inviteId").GetString(), secondIncoming.GetProperty("inviteId").GetString());
    }

    [Fact]
    public async Task Calling_Busy_User_Should_Return_CallFailed()
    {
        var caller = await CreateUser("caller-busy");
        var callee = await CreateUser("callee-busy");
        var third = await CreateUser("third-busy");

        var incomingCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdCallFailed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);
        await using var thirdHub = BuildHubConnection(third.Token);

        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingCall.TrySetResult(payload.Clone()));
        thirdHub.On<string>("CallFailed", message => thirdCallFailed.TrySetResult(message));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);
        await StartAndJoin(thirdHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var firstIncoming = await AwaitWithTimeout(incomingCall.Task, "Callee did not receive first incoming call.");
        await calleeHub.InvokeAsync("RespondToCall", firstIncoming.GetProperty("inviteId").GetString()!, true);

        await Task.Delay(200);
        await thirdHub.InvokeAsync("StartCall", calleeHub.ConnectionId);

        var failureMessage = await AwaitWithTimeout(thirdCallFailed.Task, "Third user did not receive call failed message.");
        Assert.Contains("already in another call", failureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoomParticipantsUpdated_Should_Include_All_Joined_Users_And_Reflect_Leave()
    {
        var caller = await CreateUser("caller-room");
        var callee = await CreateUser("callee-room");
        var third = await CreateUser("third-room");

        var incomingToCallee = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var incomingToThird = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerRoomUpdated = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);
        await using var thirdHub = BuildHubConnection(third.Token);

        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingToCallee.TrySetResult(payload.Clone()));
        thirdHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingToThird.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("RoomParticipantsUpdated", payload =>
        {
            var users = payload.GetProperty("users").EnumerateArray().ToList();
            if (users.Count == 3)
            {
                callerRoomUpdated.TrySetResult(payload.Clone());
            }
        });

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);
        await StartAndJoin(thirdHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var inviteForCallee = await AwaitWithTimeout(incomingToCallee.Task, "Callee did not receive incoming call.");
        await calleeHub.InvokeAsync("RespondToCall", inviteForCallee.GetProperty("inviteId").GetString()!, true);

        await callerHub.InvokeAsync("StartCall", thirdHub.ConnectionId);
        var inviteForThird = await AwaitWithTimeout(incomingToThird.Task, "Third user did not receive incoming call.");
        await thirdHub.InvokeAsync("RespondToCall", inviteForThird.GetProperty("inviteId").GetString()!, true);

        var roomWithThree = await AwaitWithTimeout(callerRoomUpdated.Task, "Room participants update with all users was not received.");
        var threeUsers = roomWithThree.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(3, threeUsers.Count);

        var roomId = roomWithThree.GetProperty("roomId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(roomId));

        var callerRoomAfterLeave = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        callerHub.On<JsonElement>("RoomParticipantsUpdated", payload =>
        {
            if (!string.Equals(payload.GetProperty("roomId").GetString(), roomId, StringComparison.Ordinal))
            {
                return;
            }

            var users = payload.GetProperty("users").EnumerateArray().ToList();
            if (users.Count == 2)
            {
                callerRoomAfterLeave.TrySetResult(payload.Clone());
            }
        });

        await thirdHub.InvokeAsync("LeaveCall");
        var roomWithTwo = await AwaitWithTimeout(callerRoomAfterLeave.Task, "Room participants update after leave was not received.");

        var remainingUsers = roomWithTwo.GetProperty("users").EnumerateArray().ToList();
        Assert.Equal(2, remainingUsers.Count);
        Assert.DoesNotContain(remainingUsers, user => string.Equals(user.GetProperty("id").GetString(), thirdHub.ConnectionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_Can_Reconnect_After_EndCall_And_Be_Called_Again()
    {
        var caller = await CreateUser("caller-reconnect");
        var callee = await CreateUser("callee-reconnect");

        var incomingFirst = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingFirst.TrySetResult(payload.Clone()));

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var firstInvite = await AwaitWithTimeout(incomingFirst.Task, "First incoming call was not received.");
        await calleeHub.InvokeAsync("RespondToCall", firstInvite.GetProperty("inviteId").GetString()!, true);

        await calleeHub.InvokeAsync("LeaveCall");
        await callerHub.InvokeAsync("LeaveCall");

        await calleeHub.StopAsync();
        await calleeHub.StartAsync();
        await calleeHub.InvokeAsync("JoinUser");

        var incomingSecond = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingSecond.TrySetResult(payload.Clone()));

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var secondInvite = await AwaitWithTimeout(incomingSecond.Task, "Incoming call after reconnect was not received.");

        Assert.NotEqual(firstInvite.GetProperty("inviteId").GetString(), secondInvite.GetProperty("inviteId").GetString());
        Assert.Equal(callerHub.ConnectionId, secondInvite.GetProperty("fromUserId").GetString());
    }

    [Fact]
    public async Task Media_State_Updates_Should_Be_Broadcast_To_Room_Participants()
    {
        var caller = await CreateUser("caller-media");
        var callee = await CreateUser("callee-media");

        var incomingCall = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerMediaUpdate = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var callerHub = BuildHubConnection(caller.Token);
        await using var calleeHub = BuildHubConnection(callee.Token);

        calleeHub.On<JsonElement>("ReceiveIncomingCall", payload => incomingCall.TrySetResult(payload.Clone()));
        callerHub.On<JsonElement>("MediaStateUpdated", payload =>
        {
            if (string.Equals(payload.GetProperty("userId").GetString(), calleeHub.ConnectionId, StringComparison.Ordinal))
            {
                callerMediaUpdate.TrySetResult(payload.Clone());
            }
        });

        await StartAndJoin(callerHub);
        await StartAndJoin(calleeHub);

        await callerHub.InvokeAsync("StartCall", calleeHub.ConnectionId);
        var invite = await AwaitWithTimeout(incomingCall.Task, "Callee did not receive incoming call.");
        await calleeHub.InvokeAsync("RespondToCall", invite.GetProperty("inviteId").GetString()!, true);

        var roomId = invite.GetProperty("roomId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(roomId));

        await calleeHub.InvokeAsync("UpdateMediaState", roomId!, false, false);

        var mediaPayload = await AwaitWithTimeout(callerMediaUpdate.Task, "Caller did not receive media state update.");
        Assert.Equal(roomId, mediaPayload.GetProperty("roomId").GetString());
        Assert.Equal(calleeHub.ConnectionId, mediaPayload.GetProperty("userId").GetString());
        Assert.False(mediaPayload.GetProperty("isCameraOn").GetBoolean());
        Assert.False(mediaPayload.GetProperty("isMicOn").GetBoolean());
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
            Password = "Password123!"
        });

        signupResponse.EnsureSuccessStatusCode();
        var signupAuth = await signupResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        return signupAuth!;
    }

    private HubConnection BuildHubConnection(string accessToken)
    {
        return new HubConnectionBuilder()
            .WithUrl(new Uri(_client.BaseAddress!, "/callHub"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult(accessToken)!;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }
}
