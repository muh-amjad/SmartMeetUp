using Livekit.Server.Sdk.Dotnet;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services;

public sealed class LiveKitService : ILiveKitService
{
    private readonly LiveKitOptions _options;
    private readonly BlobStorageOptions _blobOptions;
    private readonly ILogger<LiveKitService> _logger;
    private readonly RoomServiceClient _roomClient;
    private readonly EgressServiceClient _egressClient;

    public LiveKitService(
        IOptions<LiveKitOptions> options,
        IOptions<BlobStorageOptions> blobOptions,
        ILogger<LiveKitService> logger)
    {
        _options = options.Value;
        _blobOptions = blobOptions.Value;
        _logger = logger;

        // Admin REST client — talks to LiveKit's /twirp/livekit.RoomService endpoints
        _roomClient = new RoomServiceClient(
            _options.HttpUrl,
            _options.ApiKey,
            _options.ApiSecret);

        _egressClient = new EgressServiceClient(
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

    public async Task<string> StartCompositeEgressAsync(string roomName, string outputKey, CancellationToken ct)
    {
        var request = new RoomCompositeEgressRequest
        {
            RoomName = roomName,
            AudioOnly = true,
            // OGG container defaults to Opus audio — no separate codec field needed
            // for the audio-only room-composite request itself.
            FileOutputs =
            {
                new EncodedFileOutput
                {
                    FileType = EncodedFileType.Ogg,
                    Filepath = outputKey,
                    S3 = new S3Upload
                    {
                        AccessKey = _blobOptions.AccessKey,
                        Secret = _blobOptions.SecretKey,
                        Bucket = _blobOptions.BucketName,
                        Region = _blobOptions.Region,
                        // The egress worker runs in Docker, so it needs the compose-network
                        // endpoint here, not whatever address the API process itself uses.
                        Endpoint = _blobOptions.EgressServiceUrl,
                        ForcePathStyle = _blobOptions.ForcePathStyle,
                    },
                },
            },
        };

        var info = await _egressClient.StartRoomCompositeEgress(request);
        _logger.LogInformation("Started egress {EgressId} for room {RoomName} -> {OutputKey}",
            info.EgressId, roomName, outputKey);

        return info.EgressId;
    }

    public async Task StopEgressAsync(string egressId, CancellationToken ct)
    {
        try
        {
            await _egressClient.StopEgress(new StopEgressRequest { EgressId = egressId });
            _logger.LogInformation("Stopped egress {EgressId}", egressId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop egress {EgressId} (may have already ended)", egressId);
        }
    }
}