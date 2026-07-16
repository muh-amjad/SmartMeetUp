using System.Security.Claims;
using MeetUp.Api.Dtos;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace MeetUp.Api.Hubs;

/// <summary>
/// SignalR hub for real-time presence and meeting invites.
/// LiveKit handles all media (SDP/ICE) — this hub is just for:
///   1. Broadcasting who is online
///   2. Delivering meeting invites between users
/// </summary>
[Authorize]
public class MeetingHub : Hub
{
    private readonly IPresenceTracker _presenceTracker;
    private readonly ILogger<MeetingHub> _logger;

    public MeetingHub(IPresenceTracker presenceTracker, ILogger<MeetingHub> logger)
    {
        _presenceTracker = presenceTracker;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Meeting hub connection: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Meeting hub disconnected: {ConnectionId}", Context.ConnectionId);
        _presenceTracker.TryRemoveUser(Context.ConnectionId, out _);
        await BroadcastUsersAsync();
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Registers the authenticated user in the presence tracker so other
    /// online users see them in the "who is online" list.
    /// </summary>
    public async Task JoinUser()
    {
        var appUserId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        var username = Context.User?.Identity?.Name;
        var email = Context.User?.FindFirstValue(ClaimTypes.Email);

        if (string.IsNullOrWhiteSpace(appUserId)
            || string.IsNullOrWhiteSpace(username)
            || string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        // If the same app user is reconnecting from a new connection, drop the stale one.
        if (_presenceTracker.TryGetConnectionByAppUserId(appUserId, out var existingConnectionId)
            && !string.Equals(existingConnectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            _presenceTracker.TryRemoveUser(existingConnectionId, out _);
        }

        var user = new UserDto(Context.ConnectionId, appUserId, username, email);
        _presenceTracker.UpsertUser(user);

        _logger.LogInformation("{Username} joined the meeting hub", username);
        await BroadcastUsersAsync();
    }

    /// <summary>
    /// Sends an invite from the caller to a target participant.
    /// The caller has already created the meeting via POST /api/meetings and holds its id.
    /// </summary>
    /// <param name="targetConnectionId">Invitee's current SignalR connection id.</param>
    /// <param name="meetingId">The meeting to invite them to.</param>
    public async Task InviteToMeeting(string targetConnectionId, Guid meetingId)
    {
        var callerConnectionId = Context.ConnectionId;

        if (!_presenceTracker.TryGetUser(callerConnectionId, out var caller))
        {
            return;
        }

        if (!_presenceTracker.TryGetUser(targetConnectionId, out var callee))
        {
            await Clients.Client(callerConnectionId).SendAsync("CallFailed", "User is no longer available.");
            return;
        }

        var inviteId = Guid.NewGuid().ToString("N");
        _presenceTracker.AddInvite(new CallInvite(
            InviteId: inviteId,
            RoomId: meetingId.ToString(),
            CallerConnectionId: callerConnectionId,
            CalleeConnectionId: targetConnectionId));

        await Clients.Client(targetConnectionId).SendAsync("ReceiveInvite", new
        {
            inviteId,
            meetingId,
            fromUserId = callerConnectionId,
            fromUsername = caller.Username,
        });

        await Clients.Client(callerConnectionId).SendAsync("InviteRinging", new
        {
            inviteId,
            meetingId,
            toUserId = callee.Id,
            toUsername = callee.Username,
        });
    }

    /// <summary>
    /// Callee accepts or declines the invite. Caller is notified either way.
    /// On accept: both sides then call POST /api/meetings/{id}/join to obtain
    /// their own LiveKit tokens and connect to the media room.
    /// </summary>
    public async Task RespondToInvite(string inviteId, bool accepted)
    {
        if (!_presenceTracker.TryRemoveInvite(inviteId, out var invite))
        {
            return;
        }

        // Only the actual callee can respond — prevents spoofing
        if (invite.CalleeConnectionId != Context.ConnectionId)
        {
            return;
        }

        if (!_presenceTracker.TryGetUser(invite.CallerConnectionId, out var caller)
            || !_presenceTracker.TryGetUser(invite.CalleeConnectionId, out var callee))
        {
            return;
        }

        var meetingId = Guid.Parse(invite.RoomId);

        if (!accepted)
        {
            await Clients.Client(invite.CallerConnectionId).SendAsync("InviteDeclined", new
            {
                inviteId,
                meetingId,
                declinedByUserId = callee.Id,
                declinedByUsername = callee.Username,
            });
            return;
        }

        var acceptedPayload = new
        {
            inviteId,
            meetingId,
            acceptedByUserId = callee.Id,
            acceptedByUsername = callee.Username,
        };

        await Clients.Client(invite.CallerConnectionId).SendAsync("InviteAccepted", acceptedPayload);
        await Clients.Client(invite.CalleeConnectionId).SendAsync("InviteAccepted", acceptedPayload);
    }

    private Task BroadcastUsersAsync()
    {
        return Clients.All.SendAsync("UserJoined", _presenceTracker.GetAllUsers());
    }

    /// <summary>
    /// Called by the client when they successfully join a LiveKit room, so other
    /// users see their status change to "in a call".
    /// </summary>
    public async Task SetInCall(string meetingId)
    {
        if (!_presenceTracker.TryGetUser(Context.ConnectionId, out var user))
        {
            return;
        }

        user.IsInCall = true;
        user.RoomId = meetingId;
        _presenceTracker.UpsertUser(user);
        await BroadcastUsersAsync();
    }

    /// <summary>
    /// Called by the client when they leave the meeting so others see them as
    /// available again.
    /// </summary>
    public async Task SetLeftCall()
    {
        if (!_presenceTracker.TryGetUser(Context.ConnectionId, out var user))
        {
            return;
        }

        user.IsInCall = false;
        user.RoomId = null;
        _presenceTracker.UpsertUser(user);
        await BroadcastUsersAsync();
    }
}