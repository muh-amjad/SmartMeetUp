using System.Collections.Concurrent;

namespace MeetUp.Api.Services;

public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly ConcurrentDictionary<string, string> _appUserToConnectionMap = new();
    private readonly ConcurrentDictionary<string, (string AppUserId, string DisplayName)> _allUsers = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _rooms = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<(string FromUserId, string ToUserId)>> _pendingInvites = new();

    public void AddUser(string appUserId, string connectionId, string displayName)
    {
        _appUserToConnectionMap.TryAdd(appUserId, connectionId);
        _allUsers.TryAdd(connectionId, (appUserId, displayName));
    }

    public void RemoveUser(string connectionId)
    {
        if (_allUsers.TryRemove(connectionId, out var user))
        {
            _appUserToConnectionMap.TryRemove(user.AppUserId, out _);
        }
    }

    public bool TryGetConnectionByAppUserId(string appUserId, out string connectionId)
    {
        return _appUserToConnectionMap.TryGetValue(appUserId, out connectionId!);
    }

    public bool TryGetAppUserIdByConnectionId(string connectionId, out string appUserId)
    {
        appUserId = null!;
        if (_allUsers.TryGetValue(connectionId, out var user))
        {
            appUserId = user.AppUserId;
            return true;
        }

        return false;
    }

    public IReadOnlyList<(string AppUserId, string DisplayName, string ConnectionId)> GetAllUsers()
    {
        return _allUsers
            .Select(kvp => (kvp.Value.AppUserId, kvp.Value.DisplayName, kvp.Key))
            .ToList();
    }

    public void CreateRoom(string roomId)
    {
        _rooms.TryAdd(roomId, new ConcurrentBag<string>());
    }

    public void RemoveRoom(string roomId)
    {
        _rooms.TryRemove(roomId, out _);
    }

    public void AddUserToRoom(string roomId, string appUserId)
    {
        if (_rooms.TryGetValue(roomId, out var members))
        {
            members.Add(appUserId);
        }
    }

    public void RemoveUserFromRoom(string roomId, string appUserId)
    {
        if (_rooms.TryGetValue(roomId, out var members))
        {
            var remaining = members.Where(u => u != appUserId).ToList();
            _rooms[roomId] = new ConcurrentBag<string>(remaining);
        }
    }

    public IReadOnlyList<(string AppUserId, string DisplayName)> GetRoomMembers(string roomId)
    {
        if (!_rooms.TryGetValue(roomId, out var members))
        {
            return new List<(string, string)>();
        }

        var result = new List<(string, string)>();
        foreach (var appUserId in members)
        {
            if (_appUserToConnectionMap.TryGetValue(appUserId, out var connectionId) &&
                _allUsers.TryGetValue(connectionId, out var user))
            {
                result.Add((appUserId, user.DisplayName));
            }
        }

        return result;
    }

    public void AddPendingInvite(string roomId, string fromUserId, string toUserId)
    {
        _pendingInvites
            .GetOrAdd(roomId, _ => new ConcurrentBag<(string, string)>())
            .Add((fromUserId, toUserId));
    }

    public void RemovePendingInvite(string roomId, string fromUserId, string toUserId)
    {
        if (_pendingInvites.TryGetValue(roomId, out var invites))
        {
            var remaining = invites.Where(i => !(i.FromUserId == fromUserId && i.ToUserId == toUserId)).ToList();
            _pendingInvites[roomId] = new ConcurrentBag<(string, string)>(remaining);
        }
    }

    public IReadOnlyList<(string RoomId, string FromUserId)> GetPendingInvitesForUser(string userId)
    {
        var result = new List<(string, string)>();
        foreach (var kvp in _pendingInvites)
        {
            foreach (var invite in kvp.Value)
            {
                if (invite.ToUserId == userId)
                {
                    result.Add((kvp.Key, invite.FromUserId));
                }
            }
        }

        return result;
    }
}
