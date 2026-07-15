using System.Text.Json;
using Livekit.Server.Sdk.Dotnet;
using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Webhooks;
using MeetUp.Api.Entities;
using MeetUp.Api.Options;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Controllers;

[ApiController]
[Route("api/webhooks/livekit")]
public class LiveKitWebhookController : ControllerBase
{
    private readonly LiveKitOptions _liveKitOptions;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<LiveKitWebhookController> _logger;
    private readonly WebhookReceiver _receiver;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public LiveKitWebhookController(
        IOptions<LiveKitOptions> liveKitOptions,
        AppDbContext dbContext,
        ILogger<LiveKitWebhookController> logger)
    {
        _liveKitOptions = liveKitOptions.Value;
        _dbContext = dbContext;
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
            case "egress_ended":
                // Phase 3 will implement these
                _logger.LogInformation("Egress event {Event} received (not yet handled)", payload.Event);
                break;

            case "participant_joined":
            case "participant_left":
                // Phase 2 will implement these (need MeetingParticipant entity)
                _logger.LogDebug("Participant event {Event} received (not yet handled)", payload.Event);
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

        if (meeting.Status != MeetingStatus.Ended)
        {
            meeting.EndedUtc = DateTime.UtcNow;
            meeting.Status = MeetingStatus.Ended;   // Phase 3 mein "Processing" ho jayega
            meeting.UpdatedUtc = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            _logger.LogInformation("Meeting {MeetingId} marked Ended", meeting.Id);
        }
    }
}