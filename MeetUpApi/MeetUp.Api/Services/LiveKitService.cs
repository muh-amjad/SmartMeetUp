using Livekit.Server.Sdk.Dotnet;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services;

public sealed class LiveKitService : ILiveKitService
{
    private readonly LiveKitOptions _options;
    private readonly ILogger<LiveKitService> _logger;
    private readonly RoomServiceClient _roomClient;

    public LiveKitService(IOptions<LiveKitOptions> options, ILogger<LiveKitService> logger)
    {
        _options = options.Value;
        _logger = logger;

        // Admin REST client — talks to LiveKit's /twirp/livekit.RoomService endpoints
        _roomClient = new RoomServiceClient(
            _options.HttpUrl,
            _options.ApiKey,
            _options.ApiSecret);
    }

    public string GenerateAccessToken(string roomName, string participantIdentity, string displayName, bool isHost)
    {
        var token = new AccessToken(_options.ApiKey, _options.ApiSecret)
            .WithIdentity(participantIdentity)
            .WithName(displayName)
            .WithGrants(new VideoGrants
            {
                RoomJoin = true,
                Room = roomName,
                CanPublish = true,
                CanSubscribe = true,
                CanPublishData = true,     // chat / data messages
                RoomAdmin = isHost,        // host can mute/kick others
                RoomRecord = isHost,       // host can trigger recording (Phase 3)
            })
            .WithTtl(TimeSpan.FromHours(6));

        return token.ToJwt();
    }

    public async Task CreateRoomAsync(string roomName, CancellationToken ct)
    {
        try
        {
            var request = new CreateRoomRequest
            {
                Name = roomName,
                EmptyTimeout = 300,       // auto-close 5 min after empty
                MaxParticipants = 50,
            };
            await _roomClient.CreateRoom(request);
            _logger.LogInformation("Created LiveKit room {RoomName}", roomName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create LiveKit room {RoomName}", roomName);
            throw;
        }
    }

    public async Task EndRoomAsync(string roomName, CancellationToken ct)
    {
        try
        {
            var request = new DeleteRoomRequest { Room = roomName };
            await _roomClient.DeleteRoom(request);
            _logger.LogInformation("Deleted LiveKit room {RoomName}", roomName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete LiveKit room {RoomName} (may already be gone)", roomName);
        }
    }
}