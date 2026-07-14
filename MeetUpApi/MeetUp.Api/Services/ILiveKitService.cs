namespace MeetUp.Api.Services;

public interface ILiveKitService
{
    string GenerateAccessToken(string roomName, string participantIdentity, string displayName, bool isHost);
    Task CreateRoomAsync(string roomName, CancellationToken ct);
    Task EndRoomAsync(string roomName, CancellationToken ct);
}