using MeetUp.Api.Dtos;
using MeetUp.Api.Options;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace MeetUp.Api.Hubs
{
    [Authorize]
    public class CallHub(
        IPresenceTracker presenceTracker,
        IOptions<MeetingOptions> options,
        ILogger<CallHub> logger) : Hub
    {
        private readonly IPresenceTracker _presenceTracker = presenceTracker;
        private readonly IOptions<MeetingOptions> _options = options;
        private readonly ILogger<CallHub> _logger = logger;

        public override async Task OnConnectedAsync()
        {
            _logger.LogInformation("User connected: {ConnectionId}", Context.ConnectionId);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            _logger.LogInformation("User disconnected: {ConnectionId}", Context.ConnectionId);
            await RemoveUserFromRoomAsync(Context.ConnectionId);
            _presenceTracker.TryRemoveUser(Context.ConnectionId, out _);
            await BroadcastUsersAsync();
            await base.OnDisconnectedAsync(exception);
        }

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

            _logger.LogInformation("{Username} joined the call hub", username);

            // If this user already had a live connection, evict the stale one from any room/state.
            if (_presenceTracker.TryGetConnectionByAppUserId(appUserId, out var existingConnectionId)
                && !string.Equals(existingConnectionId, Context.ConnectionId, StringComparison.Ordinal))
            {
                await RemoveUserFromRoomAsync(existingConnectionId);
                _presenceTracker.TryRemoveUser(existingConnectionId, out _);
            }

            // Preserve any in-progress call state if the same connection re-joins.
            var hadExistingState = _presenceTracker.TryGetUser(Context.ConnectionId, out var existingConnectionUser);

            var newUser = new UserDto(Context.ConnectionId, appUserId, username, email)
            {
                IsInCall = hadExistingState && existingConnectionUser is { IsInCall: true },
                RoomId = hadExistingState ? existingConnectionUser?.RoomId : null,
            };

            _presenceTracker.UpsertUser(newUser);

            var allUsers = _presenceTracker.GetAllUsers();
            _logger.LogInformation("All users connected: {UserCount}", allUsers.Count);
            foreach (var user in allUsers)
            {
                _logger.LogDebug("Connection ID: {ConnectionId}, Username: {Username}", user.Id, user.Username);
            }

            await BroadcastUsersAsync();
        }

        public async Task StartCall(string targetUserId)
        {
            var callerId = Context.ConnectionId;

            if (!_presenceTracker.TryGetUser(callerId, out var caller))
            {
                return;
            }

            if (!_presenceTracker.TryGetUser(targetUserId, out var callee))
            {
                await Clients.Client(callerId).SendAsync("CallFailed", "User is no longer available.");
                return;
            }

            if (callee.IsInCall)
            {
                await Clients.Client(callerId).SendAsync("CallFailed", $"{callee.Username} is already in another call.");
                return;
            }

            string roomId;
            if (caller.IsInCall && !string.IsNullOrWhiteSpace(caller.RoomId))
            {
                roomId = caller.RoomId!;
                var currentRoomUsers = GetRoomUsers(roomId);
                if (currentRoomUsers.Count >= _options.Value.MaxUsersPerRoom)
                {
                    await Clients.Client(callerId).SendAsync(
                        "CallFailed",
                        $"Room is full. Max users per room is {_options.Value.MaxUsersPerRoom}.");
                    return;
                }
            }
            else
            {
                roomId = Guid.NewGuid().ToString("N");
            }

            var inviteId = Guid.NewGuid().ToString("N");
            _presenceTracker.AddInvite(new CallInvite(inviteId, roomId, callerId, targetUserId));

            await Clients.Client(targetUserId).SendAsync("ReceiveIncomingCall", new
            {
                inviteId,
                roomId,
                fromUserId = callerId,
                fromUsername = caller.Username,
            });

            await Clients.Client(callerId).SendAsync("CallRinging", new
            {
                inviteId,
                roomId,
                toUserId = callee.Id,
                toUsername = callee.Username,
            });
        }

        public async Task StartInstantMeeting()
        {
            var callerId = Context.ConnectionId;
            if (!_presenceTracker.TryGetUser(callerId, out var caller))
            {
                return;
            }

            var roomId = caller.RoomId;
            if (!caller.IsInCall || string.IsNullOrWhiteSpace(roomId))
            {
                roomId = Guid.NewGuid().ToString("N");
                caller.IsInCall = true;
                caller.RoomId = roomId;
                _presenceTracker.UpsertUser(caller);
                await Groups.AddToGroupAsync(caller.Id, roomId);
                _presenceTracker.AddToRoom(roomId, caller.Id);
            }

            var roomUsers = GetRoomUsers(roomId!);

            await Clients.Client(callerId).SendAsync("InstantMeetingStarted", new
            {
                roomId,
                users = roomUsers,
            });

            await Clients.Group(roomId!).SendAsync("RoomParticipantsUpdated", new
            {
                roomId,
                users = roomUsers,
            });

            await BroadcastUsersAsync();
        }

        public async Task RespondToCall(string inviteId, bool accepted)
        {
            if (!_presenceTracker.TryRemoveInvite(inviteId, out var invite))
            {
                return;
            }

            if (invite.CalleeConnectionId != Context.ConnectionId)
            {
                return;
            }

            if (!_presenceTracker.TryGetUser(invite.CallerConnectionId, out var caller)
                || !_presenceTracker.TryGetUser(invite.CalleeConnectionId, out var callee))
            {
                return;
            }

            if (!accepted)
            {
                await Clients.Client(invite.CallerConnectionId).SendAsync("CallDeclined", new
                {
                    inviteId,
                    roomId = invite.RoomId,
                    declinedByUserId = callee.Id,
                    declinedByUsername = callee.Username,
                });
                return;
            }

            if (!caller.IsInCall)
            {
                caller.IsInCall = true;
                caller.RoomId = invite.RoomId;
                _presenceTracker.UpsertUser(caller);
                await Groups.AddToGroupAsync(caller.Id, invite.RoomId);
                _presenceTracker.AddToRoom(invite.RoomId, caller.Id);
            }

            if (callee.IsInCall)
            {
                await Clients.Client(invite.CallerConnectionId)
                    .SendAsync("CallFailed", $"{callee.Username} is already in another call.");
                return;
            }

            callee.IsInCall = true;
            callee.RoomId = invite.RoomId;
            _presenceTracker.UpsertUser(callee);
            await Groups.AddToGroupAsync(callee.Id, invite.RoomId);
            _presenceTracker.AddToRoom(invite.RoomId, callee.Id);

            var roomUsers = GetRoomUsers(invite.RoomId);

            await Clients.Group(invite.RoomId).SendAsync("RoomParticipantsUpdated", new
            {
                roomId = invite.RoomId,
                users = roomUsers,
            });

            var acceptedPayload = new
            {
                inviteId,
                roomId = invite.RoomId,
                acceptedByUserId = callee.Id,
                acceptedByUsername = callee.Username,
                users = roomUsers,
            };

            await Clients.Client(invite.CallerConnectionId).SendAsync("CallAccepted", acceptedPayload);
            await Clients.Client(invite.CalleeConnectionId).SendAsync("CallAccepted", acceptedPayload);

            await BroadcastUsersAsync();
        }

        public async Task SendCallOffer(CallOfferDto callOffer)
        {
            if (!IsInSameRoom(callOffer.From, callOffer.To, callOffer.RoomId))
            {
                return;
            }

            _logger.LogDebug("Call offer sent from {From} to {To}", callOffer.From, callOffer.To);
            await Clients.Client(callOffer.To).SendAsync("ReceiveCallOffer", callOffer);
        }

        public async Task SendCallAnswer(CallOfferDto callOffer)
        {
            if (!IsInSameRoom(callOffer.From, callOffer.To, callOffer.RoomId))
            {
                return;
            }

            _logger.LogDebug("Call answer sent from {From} to {To}", callOffer.From, callOffer.To);
            await Clients.Client(callOffer.To).SendAsync("ReceiveCallAnswer", callOffer);
        }

        public async Task SendCandidate(string roomId, string targetUserId, object candidate)
        {
            if (!_presenceTracker.TryGetUser(Context.ConnectionId, out var sender)
                || !_presenceTracker.TryGetUser(targetUserId, out var target))
            {
                return;
            }

            if (!string.Equals(sender.RoomId, roomId, StringComparison.Ordinal)
                || !string.Equals(target.RoomId, roomId, StringComparison.Ordinal))
            {
                return;
            }

            await Clients.Client(targetUserId).SendAsync("ReceiveCandidate", new
            {
                roomId,
                from = sender.Id,
                to = targetUserId,
                candidate,
            });
        }

        public async Task UpdateMediaState(string roomId, bool isCameraOn, bool isMicOn)
        {
            if (!_presenceTracker.TryGetUser(Context.ConnectionId, out var sender)
                || string.IsNullOrWhiteSpace(sender.RoomId)
                || !string.Equals(sender.RoomId, roomId, StringComparison.Ordinal))
            {
                return;
            }

            await Clients.Group(roomId).SendAsync("MediaStateUpdated", new
            {
                roomId,
                userId = sender.Id,
                isCameraOn,
                isMicOn,
            });
        }

        public async Task LeaveCall()
        {
            var roomId = await RemoveUserFromRoomAsync(Context.ConnectionId);
            if (!string.IsNullOrWhiteSpace(roomId))
            {
                var users = GetRoomUsers(roomId);
                await Clients.Group(roomId).SendAsync("RoomParticipantsUpdated", new
                {
                    roomId,
                    users,
                });
            }

            await BroadcastUsersAsync();
        }

        private List<UserDto> GetRoomUsers(string roomId)
        {
            var connectionIds = _presenceTracker.GetRoomConnectionIds(roomId);
            if (connectionIds.Count == 0)
            {
                return new List<UserDto>();
            }

            var users = new List<UserDto>(connectionIds.Count);
            foreach (var id in connectionIds)
            {
                if (_presenceTracker.TryGetUser(id, out var user))
                {
                    // Return a copy so downstream mutation cannot affect the tracked state.
                    users.Add(new UserDto(user.Id, user.AppUserId, user.Username, user.Email, user.IsInCall, user.RoomId));
                }
            }

            return users;
        }

        private bool IsInSameRoom(string fromConnectionId, string toConnectionId, string roomId)
        {
            if (!_presenceTracker.TryGetUser(fromConnectionId, out var fromUser)
                || !_presenceTracker.TryGetUser(toConnectionId, out var toUser))
            {
                return false;
            }

            return string.Equals(fromUser.RoomId, roomId, StringComparison.Ordinal)
                && string.Equals(toUser.RoomId, roomId, StringComparison.Ordinal);
        }

        private async Task<string?> RemoveUserFromRoomAsync(string connectionId)
        {
            if (!_presenceTracker.TryGetUser(connectionId, out var user)
                || string.IsNullOrWhiteSpace(user.RoomId))
            {
                return null;
            }

            var roomId = user.RoomId!;
            user.IsInCall = false;
            user.RoomId = null;
            _presenceTracker.UpsertUser(user);

            await Groups.RemoveFromGroupAsync(connectionId, roomId);
            _presenceTracker.RemoveFromRoom(roomId, connectionId);

            return roomId;
        }

        private Task BroadcastUsersAsync()
        {
            return Clients.All.SendAsync("UserJoined", _presenceTracker.GetAllUsers());
        }
    }
}
