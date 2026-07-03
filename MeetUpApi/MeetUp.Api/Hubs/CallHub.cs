using MeetUp.Api.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;
using System.Collections.Concurrent;

namespace MeetUp.Api.Hubs
{
    [Authorize]
    public class CallHub : Hub
    {
        private sealed class CallInvite
        {
            public string InviteId { get; init; } = string.Empty;
            public string RoomId { get; init; } = string.Empty;
            public string CallerId { get; init; } = string.Empty;
            public string CalleeId { get; init; } = string.Empty;
        }

        private const int MaxUsersPerRoom = 5;
        private static readonly ConcurrentDictionary<string, UserDto> allUsers = new();
        private static readonly ConcurrentDictionary<string, string> appUserToConnectionMap = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> rooms = new();
        private static readonly ConcurrentDictionary<string, CallInvite> pendingInvites = new();

        public CallHub() { }
        
        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"User Connected: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }


        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            Console.WriteLine($"User Disconnected: {Context.ConnectionId}");
            await RemoveUserFromRoom(Context.ConnectionId);
            if (allUsers.TryRemove(Context.ConnectionId, out var disconnectedUser))
            {
                appUserToConnectionMap.TryRemove(disconnectedUser.AppUserId, out _);
            }

            await BroadcastUsers();
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

            Console.WriteLine($"{username} joined");
            if (appUserToConnectionMap.TryGetValue(appUserId, out var existingConnectionId)
                && !string.Equals(existingConnectionId, Context.ConnectionId, StringComparison.Ordinal)
                && allUsers.TryGetValue(existingConnectionId, out var staleUser))
            {
                await RemoveUserFromRoom(existingConnectionId);
                allUsers.TryRemove(existingConnectionId, out _);
            }

            var hadExistingConnectionState = allUsers.TryGetValue(Context.ConnectionId, out var existingConnectionUser);
            UserDto newUser = new UserDto(Context.ConnectionId, appUserId, username, email)
            {
                IsInCall = hadExistingConnectionState && existingConnectionUser is not null && existingConnectionUser.IsInCall,
                RoomId = hadExistingConnectionState ? existingConnectionUser?.RoomId : null,
            };

            allUsers[Context.ConnectionId] = newUser;
            appUserToConnectionMap[appUserId] = Context.ConnectionId;

            Console.WriteLine($"All users: ");
            allUsers.Values.ToList().ForEach(u =>
            {
                Console.WriteLine($"Connection ID: ${u.Id}, Username: {u.Username}");
            });

            await BroadcastUsers();
        }

        public async Task StartCall(string targetUserId)
        {
            var callerId = Context.ConnectionId;

            if (!allUsers.TryGetValue(callerId, out var caller))
            {
                return;
            }

            if (!allUsers.TryGetValue(targetUserId, out var callee))
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
                if (currentRoomUsers.Count >= MaxUsersPerRoom)
                {
                    await Clients.Client(callerId).SendAsync("CallFailed", $"Room is full. Max users per room is {MaxUsersPerRoom}.");
                    return;
                }
            }
            else
            {
                roomId = Guid.NewGuid().ToString("N");
            }

            var inviteId = Guid.NewGuid().ToString("N");
            pendingInvites[inviteId] = new CallInvite
            {
                InviteId = inviteId,
                RoomId = roomId,
                CallerId = callerId,
                CalleeId = targetUserId,
            };

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
            if (!allUsers.TryGetValue(callerId, out var caller))
            {
                return;
            }

            var roomId = caller.RoomId;
            if (!caller.IsInCall || string.IsNullOrWhiteSpace(roomId))
            {
                roomId = Guid.NewGuid().ToString("N");
                caller.IsInCall = true;
                caller.RoomId = roomId;
                await Groups.AddToGroupAsync(caller.Id, roomId);
                AddUserToRoom(roomId, caller.Id);
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

            await BroadcastUsers();
        }

        public async Task RespondToCall(string inviteId, bool accepted)
        {
            if (!pendingInvites.TryGetValue(inviteId, out var invite))
            {
                return;
            }

            if (invite.CalleeId != Context.ConnectionId)
            {
                return;
            }

            pendingInvites.TryRemove(inviteId, out _);

            if (!allUsers.TryGetValue(invite.CallerId, out var caller) || !allUsers.TryGetValue(invite.CalleeId, out var callee))
            {
                return;
            }

            if (!accepted)
            {
                await Clients.Client(invite.CallerId).SendAsync("CallDeclined", new
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
                await Groups.AddToGroupAsync(caller.Id, invite.RoomId);
                AddUserToRoom(invite.RoomId, caller.Id);
            }

            if (callee.IsInCall)
            {
                await Clients.Client(invite.CallerId).SendAsync("CallFailed", $"{callee.Username} is already in another call.");
                return;
            }

            callee.IsInCall = true;
            callee.RoomId = invite.RoomId;
            await Groups.AddToGroupAsync(callee.Id, invite.RoomId);
            AddUserToRoom(invite.RoomId, callee.Id);

            var roomUsers = GetRoomUsers(invite.RoomId);

            await Clients.Group(invite.RoomId).SendAsync("RoomParticipantsUpdated", new
            {
                roomId = invite.RoomId,
                users = roomUsers,
            });

            await Clients.Client(invite.CallerId).SendAsync("CallAccepted", new
            {
                inviteId,
                roomId = invite.RoomId,
                acceptedByUserId = callee.Id,
                acceptedByUsername = callee.Username,
                users = roomUsers,
            });

            await Clients.Client(invite.CalleeId).SendAsync("CallAccepted", new
            {
                inviteId,
                roomId = invite.RoomId,
                acceptedByUserId = callee.Id,
                acceptedByUsername = callee.Username,
                users = roomUsers,
            });

            await BroadcastUsers();
        }

        public async Task SendCallOffer(CallOfferDto callOffer)
        {
            if (!IsInSameRoom(callOffer.From, callOffer.To, callOffer.RoomId))
            {
                return;
            }

            Console.WriteLine($"Call offer sent: {callOffer}");
            await Clients.Client(callOffer.To).SendAsync("ReceiveCallOffer", callOffer);
        }

        public async Task SendCallAnswer(CallOfferDto callOffer)
        {
            if (!IsInSameRoom(callOffer.From, callOffer.To, callOffer.RoomId))
            {
                return;
            }

            Console.WriteLine($"Call answer sent: {callOffer}");
            await Clients.Client(callOffer.To).SendAsync("ReceiveCallAnswer", callOffer);
        }

        public async Task SendCandidate(string roomId, string targetUserId, object candidate)
        {
            if (!allUsers.TryGetValue(Context.ConnectionId, out var sender) || !allUsers.TryGetValue(targetUserId, out var target))
            {
                return;
            }

            if (!string.Equals(sender.RoomId, roomId, StringComparison.Ordinal) || !string.Equals(target.RoomId, roomId, StringComparison.Ordinal))
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
            if (!allUsers.TryGetValue(Context.ConnectionId, out var sender)
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
            var roomId = await RemoveUserFromRoom(Context.ConnectionId);
            if (!string.IsNullOrWhiteSpace(roomId))
            {
                var users = GetRoomUsers(roomId);
                await Clients.Group(roomId).SendAsync("RoomParticipantsUpdated", new
                {
                    roomId,
                    users,
                });
            }

            await BroadcastUsers();
        }

        private static void AddUserToRoom(string roomId, string userId)
        {
            var room = rooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, byte>());
            room[userId] = 0;
        }

        private static List<UserDto> GetRoomUsers(string roomId)
        {
            if (!rooms.TryGetValue(roomId, out var userIds))
            {
                return new List<UserDto>();
            }

            return userIds.Keys
                .Select(id => allUsers.TryGetValue(id, out var user) ? user : null)
                .Where(u => u is not null)
                .Select(u => new UserDto(u!.Id, u.AppUserId, u.Username, u.Email, u.IsInCall, u.RoomId))
                .ToList();
        }

        public static bool TryGetOnlineConnectionByAppUserId(string appUserId, out string? connectionId)
        {
            if (appUserToConnectionMap.TryGetValue(appUserId, out var mappedConnectionId)
                && allUsers.ContainsKey(mappedConnectionId))
            {
                connectionId = mappedConnectionId;
                return true;
            }

            connectionId = null;
            return false;
        }

        private static bool IsInSameRoom(string fromUserId, string toUserId, string roomId)
        {
            if (!allUsers.TryGetValue(fromUserId, out var fromUser) || !allUsers.TryGetValue(toUserId, out var toUser))
            {
                return false;
            }

            return string.Equals(fromUser.RoomId, roomId, StringComparison.Ordinal)
                && string.Equals(toUser.RoomId, roomId, StringComparison.Ordinal);
        }

        private async Task<string?> RemoveUserFromRoom(string userId)
        {
            if (!allUsers.TryGetValue(userId, out var user) || string.IsNullOrWhiteSpace(user.RoomId))
            {
                return null;
            }

            var roomId = user.RoomId;
            user.IsInCall = false;
            user.RoomId = null;

            await Groups.RemoveFromGroupAsync(userId, roomId);

            if (rooms.TryGetValue(roomId, out var roomMembers))
            {
                roomMembers.TryRemove(userId, out _);
                if (roomMembers.IsEmpty)
                {
                    rooms.TryRemove(roomId, out _);
                }
            }

            return roomId;
        }

        private Task BroadcastUsers()
        {
            return Clients.All.SendAsync("UserJoined", allUsers.Values.ToList());
        }
    }
}
