namespace MeetUp.Api.Services;

public interface ILiveKitService
{
    string GenerateAccessToken(string roomName, string participantIdentity, string displayName, bool isHost);
    Task CreateRoomAsync(string roomName, CancellationToken ct);
    Task EndRoomAsync(string roomName, CancellationToken ct);

    /// <summary>
    /// Starts a room-composite recording of the whole call — every participant's video in a grid,
    /// plus mixed audio. LiveKit's Egress worker joins the room and uploads the resulting .mp4
    /// straight to the configured S3-compatible bucket under the given object key. Participants who
    /// join later are added to the grid automatically. Returns the LiveKit-assigned egress id.
    /// </summary>
    Task<string> StartCompositeEgressAsync(string roomName, string outputKey, CancellationToken ct);

    Task StopEgressAsync(string egressId, CancellationToken ct);
}