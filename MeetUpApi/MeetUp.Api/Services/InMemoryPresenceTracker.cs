using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using MeetUp.Api.Dtos;

namespace MeetUp.Api.Services;

public sealed class InMemoryPresenceTracker : IPresenceTracker
{
    private readonly ConcurrentDictionary<string, UserDto> _usersByConnectionId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _connectionIdByAppUserId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _roomsByRoomId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CallInvite> _invitesById = new(StringComparer.Ordinal);

    public void UpsertUser(UserDto user)
    {
        _usersByConnectionId[user.Id] = user;
        _connectionIdByAppUserId[user.AppUserId] = user.Id;
    }

    public bool TryGetUser(string connectionId, [NotNullWhen(true)] out UserDto? user)
    {
        return _usersByConnectionId.TryGetValue(connectionId, out user);
    }

    public bool TryRemoveUser(string connectionId, [NotNullWhen(true)] out UserDto? user)
    {
        if (_usersByConnectionId.TryRemove(connectionId, out user))
        {
            // Only clear the app->connection mapping if it still points at this exact connection
            // (a reconnect from the same app user may have replaced the mapping already).
            _connectionIdByAppUserId.TryRemove(new KeyValuePair<string, string>(user.AppUserId, connectionId));
            return true;
        }

        return false;
    }

    public IReadOnlyCollection<UserDto> GetAllUsers()
    {
        return _usersByConnectionId.Values.ToList();
    }

    public bool TryGetConnectionByAppUserId(string appUserId, [NotNullWhen(true)] out string? connectionId)
    {
        return _connectionIdByAppUserId.TryGetValue(appUserId, out connectionId);
    }

    public void AddToRoom(string roomId, string connectionId)
    {
        var room = _roomsByRoomId.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        room[connectionId] = 0;
    }

    public void RemoveFromRoom(string roomId, string connectionId)
    {
        if (_roomsByRoomId.TryGetValue(roomId, out var room))
        {
            room.TryRemove(connectionId, out _);
            if (room.IsEmpty)
            {
                _roomsByRoomId.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, byte>>(roomId, room));
            }
        }
    }

    public IReadOnlyCollection<string> GetRoomConnectionIds(string roomId)
    {
        if (!_roomsByRoomId.TryGetValue(roomId, out var room))
        {
            return Array.Empty<string>();
        }

        return room.Keys.ToList();
    }

    public void AddInvite(CallInvite invite)
    {
        _invitesById[invite.InviteId] = invite;
    }

    public bool TryRemoveInvite(string inviteId, [NotNullWhen(true)] out CallInvite? invite)
    {
        return _invitesById.TryRemove(inviteId, out invite);
    }

    public IReadOnlyList<CallInvite> RemoveInvites(Func<CallInvite, bool> predicate)
    {
        var removed = new List<CallInvite>();
        foreach (var entry in _invitesById)
        {
            // Removing by key *and* value means two racing callers can't both claim the same invite.
            if (predicate(entry.Value) && _invitesById.TryRemove(entry))
            {
                removed.Add(entry.Value);
            }
        }

        return removed;
    }
}
