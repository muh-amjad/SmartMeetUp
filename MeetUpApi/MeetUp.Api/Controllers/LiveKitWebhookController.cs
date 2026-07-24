using Livekit.Server.Sdk.Dotnet;
using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Webhooks;
using MeetUp.Api.Entities;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MeetUp.Api.Controllers;

[ApiController]
[Route("api/webhooks/livekit")]
public class LiveKitWebhookController : ControllerBase
{
    private readonly LiveKitOptions _liveKitOptions;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<LiveKitWebhookController> _logger;
    private readonly IMeetingParticipantRepository _participantRepository;
    private readonly WebhookReceiver _receiver;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LiveKitWebhookController(
    IOptions<LiveKitOptions> liveKitOptions,
    AppDbContext dbContext,
    IMeetingParticipantRepository participantRepository,
    ILogger<LiveKitWebhookController> logger)
    {
        _liveKitOptions = liveKitOptions.Value;
        _dbContext = dbContext;
        _participantRepository = participantRepository;
        _logger = logger;
        _receiver = new WebhookReceiver(_liveKitOptions.ApiKey, _liveKitOptions.ApiSecret);
    }

    /// <summary>
    /// LiveKit posts events here whenever room/participant/egress state changes.
    /// Signature is verified via the SDK's WebhookReceiver — HMAC of the body
    /// signed with our ApiSecret.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        // Read raw body so we can hand it to the signature verifier
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync(ct);
        Request.Body.Position = 0;

        var authHeader = Request.Headers.Authorization.ToString();

        // DEBUG: log incoming request details before verification
        var authPreview = string.IsNullOrEmpty(authHeader)
            ? "(empty)"
            : authHeader.Length > 30
                ? authHeader.Substring(0, 30) + "..."
                : authHeader;
        _logger.LogInformation(
            "LiveKit webhook INCOMING: bodyLength={BodyLength}, authPreview={AuthPreview}, contentType={ContentType}",
            body.Length, authPreview, Request.ContentType);

        if (string.IsNullOrWhiteSpace(authHeader))
        {
            _logger.LogWarning("LiveKit webhook missing Authorization header");
            return Unauthorized();
        }

        WebhookEvent evt;
        try
        {
            // Throws if the signature doesn't match — rejects spoofed calls
            evt = _receiver.Receive(body, authHeader);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LiveKit webhook signature verification failed. Error: {ErrorMessage}",
                ex.Message);
            return Unauthorized();
        }

        // Parse the raw JSON too — SDK's WebhookEvent has some fields but we
        // want direct access to Room.Name etc. via our own DTO.
        var payload = JsonSerializer.Deserialize<LiveKitWebhookEventDto>(body, JsonOptions)
                      ?? new LiveKitWebhookEventDto();

        _logger.LogInformation(
            "LiveKit webhook received: event={Event}, room={RoomName}",
            payload.Event, payload.Room?.Name);

        switch (payload.Event)
        {
            case "room_started":
                await HandleRoomStartedAsync(payload, ct);
                break;

            case "room_finished":
                await HandleRoomFinishedAsync(payload, ct);
                break;

            case "egress_started":
                _logger.LogInformation("Egress {EgressId} started for room {RoomName}",
                    payload.EgressInfo?.EgressId, payload.EgressInfo?.RoomName);
                break;

            case "egress_ended":
                await HandleEgressEndedAsync(payload, ct);
                break;

            case "participant_joined":
                await HandleParticipantJoinedAsync(payload, ct);
                break;

            case "participant_left":
                await HandleParticipantLeftAsync(payload, ct);
                break;

            default:
                _logger.LogDebug("Unhandled LiveKit event: {Event}", payload.Event);
                break;
        }

        return Ok();
    }

    private async Task HandleRoomStartedAsync(LiveKitWebhookEventDto payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Room?.Name))
        {
            return;
        }

        var meeting = await _dbContext.Meetings
            .FirstOrDefaultAsync(m => m.LiveKitRoomName == payload.Room.Name, ct);

        if (meeting is null)
        {
            _logger.LogWarning("room_started for unknown room {RoomName}", payload.Room.Name);
            return;
        }

        // Idempotent: only update if we haven't recorded a start time yet
        if (!meeting.ActualStartUtc.HasValue)
        {
            meeting.ActualStartUtc = DateTime.UtcNow;
            meeting.Status = MeetingStatus.Live;
            meeting.UpdatedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting {MeetingId} marked Live", meeting.Id);
        }
    }

    private async Task HandleRoomFinishedAsync(LiveKitWebhookEventDto payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Room?.Name))
        {
            return;
        }

        var meeting = await _dbContext.Meetings
            .FirstOrDefaultAsync(m => m.LiveKitRoomName == payload.Room.Name, ct);

        if (meeting is null)
        {
            _logger.LogWarning("room_finished for unknown room {RoomName}", payload.Room.Name);
            return;
        }

        if (meeting.Status != MeetingStatus.Ended && meeting.Status != MeetingStatus.Processing)
        {
            meeting.EndedUtc = DateTime.UtcNow;
            meeting.Status = MeetingStatus.Processing;
            meeting.UpdatedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting {MeetingId} marked Processing (recording pipeline will run)", meeting.Id);
        }
    }

    private async Task HandleEgressEndedAsync(LiveKitWebhookEventDto payload, CancellationToken ct)
    {
        var egressId = payload.EgressInfo?.EgressId;
        if (string.IsNullOrWhiteSpace(egressId))
        {
            return;
        }

        // Match on EgressId (set when we started the recording in MeetingsController.Create)
        // rather than room name, since a room can theoretically be re-recorded.
        var meeting = await _dbContext.Meetings
            .FirstOrDefaultAsync(m => m.EgressId == egressId, ct);

        if (meeting is null)
        {
            _logger.LogWarning("egress_ended for unknown egress {EgressId}", egressId);
            return;
        }

        var fileResult = payload.EgressInfo?.FileResults.FirstOrDefault();
        if (fileResult is not null)
        {
            meeting.RecordingBlobKey = fileResult.Filename;
            meeting.RecordingDurationSeconds = (int)(fileResult.DurationNanoseconds / 1_000_000_000);
        }
        else
        {
            _logger.LogWarning("egress_ended for {EgressId} had no file results (recording may have failed)", egressId);
        }

        // room_finished already moves Live -> Processing; this just keeps it there in case
        // egress reports back before/without a room_finished event for some reason.
        if (meeting.Status is MeetingStatus.Live or MeetingStatus.Ended)
        {
            meeting.Status = MeetingStatus.Processing;
        }

        meeting.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        // Transcription (Phase 4) picks up from here — not implemented yet, so we stop at
        // "recording is safely in blob storage" for now instead of enqueuing a job type
        // that doesn't exist.
        _logger.LogInformation(
            "Meeting {MeetingId} recording saved: key={BlobKey}, durationSec={Duration}",
            meeting.Id, meeting.RecordingBlobKey, meeting.RecordingDurationSeconds);
    }

    private async Task HandleParticipantJoinedAsync(LiveKitWebhookEventDto payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Room?.Name) ||
            string.IsNullOrWhiteSpace(payload.Participant?.Identity))
        {
            return;
        }

        var meeting = await _dbContext.Meetings
            .FirstOrDefaultAsync(m => m.LiveKitRoomName == payload.Room.Name, ct);

        if (meeting is null)
        {
            _logger.LogWarning("participant_joined for unknown room {RoomName}", payload.Room.Name);
            return;
        }

        var userId = payload.Participant.Identity;
        var existing = await _participantRepository.GetByMeetingAndUserAsync(meeting.Id, userId, ct);

        if (existing is null)
        {
            var isHost = string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal);
            var participant = new MeetingParticipant
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                UserId = userId,
                Role = isHost ? ParticipantRole.Host : ParticipantRole.Participant,
                JoinedUtc = DateTime.UtcNow,
            };
            await _participantRepository.AddAsync(participant, ct);
        }
        else
        {
            // Rejoin scenario — clear LeftUtc, refresh JoinedUtc if missing
            existing.LeftUtc = null;
            existing.JoinedUtc ??= DateTime.UtcNow;
        }

        await _participantRepository.SaveChangesAsync(ct);

        _logger.LogInformation("Participant {UserId} joined meeting {MeetingId}", userId, meeting.Id);
    }

    private async Task HandleParticipantLeftAsync(LiveKitWebhookEventDto payload, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(payload.Room?.Name) ||
            string.IsNullOrWhiteSpace(payload.Participant?.Identity))
        {
            return;
        }

        var meeting = await _dbContext.Meetings
            .FirstOrDefaultAsync(m => m.LiveKitRoomName == payload.Room.Name, ct);

        if (meeting is null)
        {
            return;
        }

        var participant = await _participantRepository.GetByMeetingAndUserAsync(meeting.Id, payload.Participant.Identity, ct);
        if (participant is null)
        {
            _logger.LogWarning("participant_left for unknown participant {UserId} in room {RoomName}",
                payload.Participant.Identity, payload.Room.Name);
            return;
        }

        participant.LeftUtc = DateTime.UtcNow;
        await _participantRepository.SaveChangesAsync(ct);

        _logger.LogInformation("Participant {UserId} left meeting {MeetingId}", participant.UserId, meeting.Id);
    }
}