using System.Text.Json.Serialization;

namespace MeetUp.Api.Dtos.Webhooks;

/// <summary>
/// Minimal shape of LiveKit's webhook payload — we only bind the fields we care about.
/// See https://docs.livekit.io/home/server/webhooks/ for the full schema.
/// </summary>
public sealed class LiveKitWebhookEventDto
{
    /// <summary>Event type: "room_started", "room_finished", "egress_ended", etc.</summary>
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty;

    /// <summary>Room info — populated for room_* events.</summary>
    [JsonPropertyName("room")]
    public RoomInfoDto? Room { get; set; }

    /// <summary>Egress info — populated for egress_* events (Phase 3).</summary>
    [JsonPropertyName("egressInfo")]
    public EgressInfoDto? EgressInfo { get; set; }

    // LiveKit sends int64 fields as JSON strings (protobuf JSON mapping), not numbers.
    [JsonPropertyName("createdAt")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long CreatedAt { get; set; }
}

public sealed class RoomInfoDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sid")]
    public string Sid { get; set; } = string.Empty;

    /// <summary>Unix milliseconds when the room was created.</summary>
    [JsonPropertyName("creationTime")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long CreationTime { get; set; }
}

public sealed class EgressInfoDto
{
    [JsonPropertyName("egressId")]
    public string EgressId { get; set; } = string.Empty;

    [JsonPropertyName("roomName")]
    public string RoomName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}