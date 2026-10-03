using System.Diagnostics.CodeAnalysis;
using MeetUp.Api.Dtos;

namespace MeetUp.Api.Services;

/// <summary>
/// Tracks connected users, room membership, and pending call invites for the SignalR hub.
/// The in-memory implementation is registered as a singleton so it survives across hub instances.
/// A distributed implementation (e.g. Redis) can be swapped in for horizontal scaling.
/// </summary>
public interface IPresenceTracker
{
    // Users (keyed by SignalR connection id)
    void UpsertUser(UserDto user);
    bool TryGetUser(string connectionId, [NotNullWhen(true)] out UserDto? user);
    bool TryRemoveUser(string connectionId, [NotNullWhen(true)] out UserDto? user);
    IReadOnlyCollection<UserDto> GetAllUsers();
    bool TryGetConnectionByAppUserId(string appUserId, [NotNullWhen(true)] out string? connectionId);

    // Rooms (values are connection ids)
    void AddToRoom(string roomId, string connectionId);
    void RemoveFromRoom(string roomId, string connectionId);
    IReadOnlyCollection<string> GetRoomConnectionIds(string roomId);

    // Pending call invites (keyed by invite id)
    void AddInvite(CallInvite invite);
    bool TryRemoveInvite(string inviteId, [NotNullWhen(true)] out CallInvite? invite);

    /// <summary>Removes and returns every pending invite that matches. Each invite is returned by at most one caller.</summary>
    IReadOnlyList<CallInvite> RemoveInvites(Func<CallInvite, bool> predicate);
}

public sealed record CallInvite(string InviteId, string RoomId, string CallerConnectionId, string CalleeConnectionId);
