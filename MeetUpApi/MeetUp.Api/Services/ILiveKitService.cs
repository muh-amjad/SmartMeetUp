namespace MeetUp.Api.Services;

public interface ILiveKitService
{
    string GenerateAccessToken(string roomName, string participantIdentity, string displayName, bool isHost);
    Task CreateRoomAsync(string roomName, CancellationToken ct);
    Task EndRoomAsync(string roomName, CancellationToken ct);

    /// <summary>
    /// Starts an audio-only room-composite recording. LiveKit's Egress worker joins the room
    /// and uploads the resulting .ogg file straight to the configured S3-compatible bucket
    /// under the given object key. Returns the LiveKit-assigned egress id.
    /// </summary>
    Task<string> StartCompositeEgressAsync(string roomName, string outputKey, CancellationToken ct);

    Task StopEgressAsync(string egressId, CancellationToken ct);
}