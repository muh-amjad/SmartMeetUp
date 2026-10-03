using MeetUp.Api.Dtos;
using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

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
    private readonly IChatMessageRepository _chatMessageRepository;
    private readonly IMeetingRepository _meetingRepository;
    private readonly IMeetingParticipantRepository _participantRepository;
    private readonly IMeetingAnalyticsRepository _analyticsRepository;
    private readonly ILogger<MeetingHub> _logger;

    private const int MaxChatMessageLength = 2000;
    private const int MaxSpeakingIntervalsPerCall = 200;
    private static string GroupName(Guid meetingId) => $"meeting-{meetingId:N}";

    public MeetingHub(
    IPresenceTracker presenceTracker,
    IChatMessageRepository chatMessageRepository,
    IMeetingRepository meetingRepository,
    IMeetingParticipantRepository participantRepository,
    IMeetingAnalyticsRepository analyticsRepository,
    ILogger<MeetingHub> logger)
    {
        _presenceTracker = presenceTracker;
        _chatMessageRepository = chatMessageRepository;
        _meetingRepository = meetingRepository;
        _participantRepository = participantRepository;
        _analyticsRepository = analyticsRepository;
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
        _presenceTracker.TryRemoveUser(Context.ConnectionId, out var user);

        // A closed tab or lost network must not leave anyone's phone ringing for a call that is
        // gone, nor leave a caller waiting on someone who can no longer answer.
        await CancelInvitesFromAsync(Context.ConnectionId);
        await MissInvitesToAsync(Context.ConnectionId, user?.Username);

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
    /// Sends an invite from the caller to a target participant. Used both to start a call (the
    /// caller has just created the meeting) and to add someone to a call already in progress —
    /// the invite always names an existing meeting, and the invitee joins that same room.
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

        // From inside a call you can only add people to that call. Calling someone into a different
        // meeting means leaving this one first.
        if (caller.IsInCall
            && !string.IsNullOrEmpty(caller.RoomId)
            && !string.Equals(caller.RoomId, meetingId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await Clients.Client(callerConnectionId).SendAsync(
                "CallFailed", "Leave your current call before calling someone else.");
            return;
        }

        // Only someone who is part of the meeting can bring others into it. Without this, any
        // signed-in user who learned a meeting id could pull people into a call they are not in.
        var ct = Context.ConnectionAborted;
        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct);
        if (meeting is null
            || meeting.EndedUtc.HasValue
            || meeting.Status is not (MeetingStatus.Scheduled or MeetingStatus.Live))
        {
            await Clients.Client(callerConnectionId).SendAsync("CallFailed", "This meeting has already ended.");
            return;
        }

        if (await _participantRepository.GetByMeetingAndUserAsync(meetingId, caller.AppUserId, ct) is null)
        {
            _logger.LogWarning(
                "InviteToMeeting rejected: user {UserId} is not a participant of meeting {MeetingId}",
                caller.AppUserId, meetingId);
            await Clients.Client(callerConnectionId).SendAsync("CallFailed", "You are not part of this meeting.");
            return;
        }

        if (string.Equals(callee.RoomId, meetingId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            await Clients.Client(callerConnectionId).SendAsync(
                "CallFailed", $"{callee.Username} is already in this call.");
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
    /// <returns>
    /// False when the invite no longer stands (the caller hung up, or it was already answered),
    /// so the callee does not walk into a call that is not there.
    /// </returns>
    public async Task<bool> RespondToInvite(string inviteId, bool accepted)
    {
        // Matched on the callee as well, so only the person invited can answer — or remove — it.
        var invite = _presenceTracker
            .RemoveInvites(i => i.InviteId == inviteId && i.CalleeConnectionId == Context.ConnectionId)
            .FirstOrDefault();
        if (invite is null)
        {
            return false;
        }

        if (!_presenceTracker.TryGetUser(invite.CallerConnectionId, out var caller)
            || !_presenceTracker.TryGetUser(invite.CalleeConnectionId, out var callee))
        {
            return false;
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
            return true;
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
        return true;
    }

    /// <summary>
    /// The callee let the call ring out without answering. The caller is told it was missed —
    /// which reads differently from a deliberate decline.
    /// </summary>
    public async Task MissInvite(string inviteId)
    {
        var invite = _presenceTracker
            .RemoveInvites(i => i.InviteId == inviteId && i.CalleeConnectionId == Context.ConnectionId)
            .FirstOrDefault();
        if (invite is null)
        {
            return;
        }

        _presenceTracker.TryGetUser(Context.ConnectionId, out var callee);
        await NotifyCallerOfMissAsync(invite, callee?.Username);
    }

    private Task BroadcastUsersAsync()
    {
        return Clients.All.SendAsync("UserJoined", _presenceTracker.GetAllUsers());
    }

    /// <summary>
    /// Withdraws invites this connection sent that are still ringing, so the callee's ringing
    /// stops. <paramref name="exceptMeetingId"/> keeps invites to a meeting the caller is still in.
    /// </summary>
    private async Task CancelInvitesFromAsync(string callerConnectionId, string? exceptMeetingId = null)
    {
        var cancelled = _presenceTracker.RemoveInvites(i =>
            i.CallerConnectionId == callerConnectionId
            && (exceptMeetingId is null
                || !string.Equals(i.RoomId, exceptMeetingId, StringComparison.OrdinalIgnoreCase)));

        foreach (var invite in cancelled)
        {
            await Clients.Client(invite.CalleeConnectionId).SendAsync("InviteCancelled", new
            {
                inviteId = invite.InviteId,
                meetingId = Guid.Parse(invite.RoomId),
            });
        }
    }

    /// <summary>Tells callers that this connection will never answer the invites it was sent.</summary>
    private async Task MissInvitesToAsync(string calleeConnectionId, string? calleeUsername)
    {
        var missed = _presenceTracker.RemoveInvites(i => i.CalleeConnectionId == calleeConnectionId);
        foreach (var invite in missed)
        {
            await NotifyCallerOfMissAsync(invite, calleeUsername);
        }
    }

    private Task NotifyCallerOfMissAsync(CallInvite invite, string? calleeUsername)
    {
        return Clients.Client(invite.CallerConnectionId).SendAsync("InviteMissed", new
        {
            inviteId = invite.InviteId,
            meetingId = Guid.Parse(invite.RoomId),
            missedByUserId = invite.CalleeConnectionId,
            missedByUsername = calleeUsername ?? "They",
        });
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

        // Switching straight from one call to another (accepting an invite while already in a
        // call) never passes through SetLeftCall, so drop the old meeting's group here — otherwise
        // this connection keeps receiving the previous meeting's chat.
        if (Guid.TryParse(user.RoomId, out var previousMeetingGuid)
            && !string.Equals(user.RoomId, meetingId, StringComparison.OrdinalIgnoreCase))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(previousMeetingGuid));
        }

        user.IsInCall = true;
        user.RoomId = meetingId;
        _presenceTracker.UpsertUser(user);

        // Add the connection to this meeting's SignalR group so it receives
        // chat + future room-scoped events.
        if (Guid.TryParse(meetingId, out var meetingGuid))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(meetingGuid));
        }

        // Moving to another call abandons the previous one, along with anyone still being rung into it.
        await CancelInvitesFromAsync(Context.ConnectionId, exceptMeetingId: meetingId);

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

        // Capture the group before we clear it
        var previousRoomId = user.RoomId;

        user.IsInCall = false;
        user.RoomId = null;
        _presenceTracker.UpsertUser(user);

        if (Guid.TryParse(previousRoomId, out var previousMeetingGuid))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(previousMeetingGuid));
        }

        // Hanging up stops the ringing on everyone this caller was still trying to reach.
        await CancelInvitesFromAsync(Context.ConnectionId);

        await BroadcastUsersAsync();
    }

    /// <summary>
    /// Persist a chat message and broadcast it to all connections in the
    /// meeting's SignalR group. Sender must currently be in this meeting
    /// (per PresenceTracker) — otherwise the call is silently dropped.
    /// </summary>
    public async Task SendChatMessage(Guid meetingId, string text)
    {
        // Basic sanity checks (defensive against malicious clients)
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var trimmed = text.Trim();
        if (trimmed.Length > MaxChatMessageLength)
        {
            trimmed = trimmed[..MaxChatMessageLength];  // truncate, don't reject
        }

        if (!_presenceTracker.TryGetUser(Context.ConnectionId, out var user))
        {
            return;
        }

        // User must be currently in this meeting to chat in it
        if (!string.Equals(user.RoomId, meetingId.ToString(), StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "SendChatMessage rejected: user {UserId} not in meeting {MeetingId} (RoomId={RoomId})",
                user.AppUserId, meetingId, user.RoomId);
            return;
        }

        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            SenderUserId = user.AppUserId,
            Text = trimmed,
            SentUtc = DateTime.UtcNow,
        };

        await _chatMessageRepository.AddAsync(message, Context.ConnectionAborted);
        await _chatMessageRepository.SaveChangesAsync(Context.ConnectionAborted);

        // Broadcast to everyone currently in this meeting
        await Clients.Group(GroupName(meetingId)).SendAsync("ChatMessageReceived", new
        {
            id = message.Id,
            meetingId,
            senderUserId = user.AppUserId,
            senderUsername = user.Username,
            text = message.Text,
            sentUtc = message.SentUtc,
        });
    }

    /// <summary>
    /// Records which stretches of the meeting the caller was actively speaking for, so that the
    /// transcript's anonymous "Speaker A/B" labels can later be attributed to real accounts.
    ///
    /// This comes from the client rather than a LiveKit webhook because LiveKit only reports
    /// active-speaker changes over the realtime signalling channel to connected SDKs — its webhook
    /// payload carries no speaker information at all. The browser SDK sees these events, so the
    /// browser reports them here.
    ///
    /// Intervals are always attributed to the authenticated caller, never to an id they supply,
    /// so a client cannot fabricate speaking time for someone else.
    ///
    /// Membership is checked against the meeting's participant list in the database, not against
    /// in-memory presence. Presence is rebuilt from scratch whenever a connection reconnects or the
    /// API restarts, and for a while afterwards the caller looks like they are in no meeting at all.
    /// Gating on it silently threw away every interval reported in that window — for whole calls,
    /// whenever it happened early — which is how one participant's lines stayed "Speaker B" while
    /// everyone else's were named. The participant row is written on join and survives all of that.
    /// </summary>
    public async Task ReportSpeakingIntervals(Guid meetingId, SpeakingIntervalDto[] intervals)
    {
        if (intervals is null || intervals.Length == 0)
        {
            return;
        }

        var userId = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        var ct = Context.ConnectionAborted;

        if (await _participantRepository.GetByMeetingAndUserAsync(meetingId, userId, ct) is null)
        {
            _logger.LogWarning(
                "ReportSpeakingIntervals rejected: user {UserId} is not a participant of meeting {MeetingId}",
                userId, meetingId);
            return;
        }

        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct);
        if (meeting is null)
        {
            return;
        }

        // Transcript timestamps are relative to when the recording started, so rebase the client's
        // wall clock onto the meeting's actual start. Both origins are set within a second or so of
        // each other; speaker matching compares relative overlap, so a small shared offset washes out.
        var origin = meeting.ActualStartUtc ?? meeting.CreatedUtc;

        var rows = new List<ParticipantAudioActivity>();
        foreach (var interval in intervals.Take(MaxSpeakingIntervalsPerCall))
        {
            var startMs = (int)Math.Max(0, (interval.StartedUtc - origin).TotalMilliseconds);
            var stopMs = (int)Math.Max(0, (interval.StoppedUtc - origin).TotalMilliseconds);

            if (stopMs <= startMs)
            {
                continue;   // clock skew or a zero-length blip — nothing useful to store
            }

            rows.Add(new ParticipantAudioActivity
            {
                Id = Guid.NewGuid(),
                MeetingId = meetingId,
                UserId = userId,
                StartedSpeakingMs = startMs,
                StoppedSpeakingMs = stopMs,
            });
        }

        if (rows.Count == 0)
        {
            return;
        }

        await _analyticsRepository.AddAudioActivityAsync(rows, ct);
        await _analyticsRepository.SaveChangesAsync(ct);

        _logger.LogDebug("Recorded {Count} speaking interval(s) for user {UserId} in meeting {MeetingId}",
            rows.Count, userId, meetingId);
    }
}