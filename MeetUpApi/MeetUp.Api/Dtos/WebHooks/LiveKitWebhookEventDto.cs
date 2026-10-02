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

    [JsonPropertyName("participant")]
    public ParticipantInfoDto? Participant { get; set; }

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

public sealed class ParticipantInfoDto
{
    /// <summary>Participant identity — corresponds to AspNetUsers.Id (we set it in AccessToken.WithIdentity).</summary>
    [JsonPropertyName("identity")]
    public string Identity { get; set; } = string.Empty;

    /// <summary>Display name — we set it via AccessToken.WithName (usually username).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>LiveKit-generated stable id, useful for debugging.</summary>
    [JsonPropertyName("sid")]
    public string Sid { get; set; } = string.Empty;
}

public sealed class EgressInfoDto
{
    [JsonPropertyName("egressId")]
    public string EgressId { get; set; } = string.Empty;

    [JsonPropertyName("roomName")]
    public string RoomName { get; set; } = string.Empty;

    /// <summary>
    /// Protobuf enum name, e.g. "EGRESS_COMPLETE" or "EGRESS_ABORTED". Protobuf JSON omits default
    /// values, so the first enum member (EGRESS_STARTING) arrives as an empty string.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Why the recording failed or was aborted, e.g. "Start signal not received".</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>One entry per configured file output — we only ever configure one (the room recording).</summary>
    [JsonPropertyName("fileResults")]
    public List<EgressFileResultDto> FileResults { get; set; } = new();
}

public sealed class EgressFileResultDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    /// <summary>Nanoseconds — LiveKit reports durations at nanosecond precision.</summary>
    [JsonPropertyName("duration")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long DurationNanoseconds { get; set; }

    /// <summary>Unix nanoseconds when this file's recording began — the transcript's zero point.</summary>
    [JsonPropertyName("startedAt")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long StartedAtNanoseconds { get; set; }

    [JsonPropertyName("size")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long SizeBytes { get; set; }
}