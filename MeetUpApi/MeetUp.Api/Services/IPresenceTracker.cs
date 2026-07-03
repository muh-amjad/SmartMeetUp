namespace MeetUp.Api.Services;

public interface IPresenceTracker
{
    void AddUser(string appUserId, string connectionId, string displayName);
    void RemoveUser(string connectionId);
    bool TryGetConnectionByAppUserId(string appUserId, out string connectionId);
    bool TryGetAppUserIdByConnectionId(string connectionId, out string appUserId);
    IReadOnlyList<(string AppUserId, string DisplayName, string ConnectionId)> GetAllUsers();
    void CreateRoom(string roomId);
    void RemoveRoom(string roomId);
    void AddUserToRoom(string roomId, string appUserId);
    void RemoveUserFromRoom(string roomId, string appUserId);
    IReadOnlyList<(string AppUserId, string DisplayName)> GetRoomMembers(string roomId);
    void AddPendingInvite(string roomId, string fromUserId, string toUserId);
    void RemovePendingInvite(string roomId, string fromUserId, string toUserId);
    IReadOnlyList<(string RoomId, string FromUserId)> GetPendingInvitesForUser(string userId);
}
