using Hangfire;
using Livekit.Server.Sdk.Dotnet;
using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Webhooks;
using MeetUp.Api.Entities;
using MeetUp.Api.Jobs;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
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
    private readonly TokenVerifier _tokenVerifier;

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
        _tokenVerifier = new TokenVerifier(_liveKitOptions.ApiKey, _liveKitOptions.ApiSecret);
    }

    /// <summary>
    /// LiveKit posts events here whenever room/participant/egress state changes.
    /// The Authorization header is a JWT signed with our ApiSecret that carries a SHA-256 of the
    /// body; both are checked before anything in the body is trusted.
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

        // Verified here rather than with the SDK's WebhookReceiver.Receive. That method also parses
        // the body into the SDK's own event type, strictly — and LiveKit Cloud runs a newer server
        // than this SDK version knows, adding fields such as participant "capabilities" and
        // room_finished's "roomEndReason". Parsing failed on them, the failure was reported as a bad
        // signature, and those events were dropped: losing room_finished meant a meeting never got
        // an end time and could still be rejoined. Its ignoreUnknownFields flag does not help in
        // this SDK version. Nothing here needs the SDK's event object anyway — the body is read
        // below with our own DTO, which ignores fields it does not know.
        if (!IsSignedByLiveKit(body, authHeader, out var failure))
        {
            _logger.LogWarning("LiveKit webhook rejected: {Reason}", failure);
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

    /// <summary>
    /// The same two checks the SDK's WebhookReceiver makes: the token's signature, issuer and
    /// expiry (via the SDK's TokenVerifier), and that the body is exactly what was signed.
    /// </summary>
    private bool IsSignedByLiveKit(string body, string authHeader, out string failure)
    {
        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader["Bearer ".Length..]
            : authHeader;

        ClaimsModel claims;
        try
        {
            claims = _tokenVerifier.Verify(token);
        }
        catch (Exception ex)
        {
            failure = $"invalid token ({ex.Message})";
            return false;
        }

        if (string.IsNullOrEmpty(claims.Sha256))
        {
            failure = "token carries no body checksum";
            return false;
        }

        byte[] signedHash;
        try
        {
            signedHash = Convert.FromBase64String(claims.Sha256);
        }
        catch (FormatException)
        {
            failure = "body checksum is not valid base64";
            return false;
        }

        var actualHash = SHA256.HashData(Encoding.UTF8.GetBytes(body));
        if (!CryptographicOperations.FixedTimeEquals(signedHash, actualHash))
        {
            failure = "body does not match its signed checksum";
            return false;
        }

        failure = string.Empty;
        return true;
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

        // The call is over whatever the recording's state, so the end time is always recorded. It is
        // what stops anyone rejoining — previously it was skipped whenever the recording's outcome
        // had arrived first, and a finished meeting could be reopened.
        meeting.EndedUtc ??= DateTime.UtcNow;

        // Only a meeting that is still running changes status here. The two webhooks race: when the
        // recording outcome arrives first it has already settled the status (Ended, Failed, or
        // Processing with a transcript queued), and overwriting that would strand the meeting.
        //
        // Processing means "a recording is on its way" and is only right when one is: the meeting
        // list will not open a Processing meeting, so one with no recording coming would be locked
        // there for good. EgressId is cleared when a recording fails mid-call (see egress_ended).
        if (meeting.Status is MeetingStatus.Scheduled or MeetingStatus.Live)
        {
            meeting.Status = meeting.EgressId is null ? MeetingStatus.Ended : MeetingStatus.Processing;

            _logger.LogInformation(
                "Meeting {MeetingId} finished: {Status}", meeting.Id,
                meeting.Status == MeetingStatus.Processing ? "Processing (recording pipeline will run)" : "Ended (no recording)");
        }

        meeting.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);
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

        var status = payload.EgressInfo?.Status ?? string.Empty;
        var fileResult = payload.EgressInfo?.FileResults.FirstOrDefault();

        // egress_ended fires for every way a recording can stop, not just success — and an aborted
        // recording still reports the filename it *would* have written. Trusting that filename is
        // what made failed recordings look successful: transcription then ran against a file that
        // was never uploaded, failed, and the meeting surfaced as a failed analysis rather than as
        // the recording problem it actually was. LIMIT_REACHED is kept: the file is complete up to
        // the limit.
        var completed = status is "EGRESS_COMPLETE" or "EGRESS_LIMIT_REACHED";
        var hasMedia = fileResult is not null
            && !string.IsNullOrWhiteSpace(fileResult.Filename)
            && fileResult.DurationNanoseconds > 0;

        if (completed && hasMedia)
        {
            meeting.RecordingBlobKey = fileResult!.Filename;
            meeting.RecordingDurationSeconds = (int)(fileResult.DurationNanoseconds / 1_000_000_000);
            if (fileResult.StartedAtNanoseconds > 0)
            {
                meeting.RecordingStartedUtc = DateTime.UnixEpoch.AddTicks(fileResult.StartedAtNanoseconds / 100);
            }

            // room_finished already moves Live -> Processing; this just keeps it there in case
            // egress reports back before/without a room_finished event for some reason.
            if (meeting.Status is MeetingStatus.Live or MeetingStatus.Ended)
            {
                meeting.Status = MeetingStatus.Processing;
            }
        }
        else if (meeting.Status is MeetingStatus.Scheduled or MeetingStatus.Live)
        {
            // The recording stopped but the call has not: people are still in it. Ending the
            // meeting here would turn away everyone who tries to join for the rest of the call.
            // Forgetting the egress instead lets room_finished close it as a meeting without a
            // recording, rather than as one waiting for a recording that will never arrive.
            meeting.EgressId = null;

            _logger.LogWarning(
                "Meeting {MeetingId}: recording stopped during the call (egress {EgressId}, status " +
                "{Status}: {Error}); the meeting continues unrecorded",
                meeting.Id, egressId, status, payload.EgressInfo?.Error);
        }
        else
        {
            // Aborted with nothing captured (typically "Start signal not received": nobody ever
            // published audio or video) is a meeting that simply has no recording. A genuine
            // failure is surfaced as one. Either way nothing is queued, so the meeting no longer
            // sits in Processing forever waiting for a transcript that cannot come.
            var failed = status == "EGRESS_FAILED" || (completed && !hasMedia);
            meeting.Status = failed ? MeetingStatus.Failed : MeetingStatus.Ended;
            meeting.EndedUtc ??= DateTime.UtcNow;

            _logger.LogWarning(
                "Meeting {MeetingId} has no usable recording: egress {EgressId} ended with status " +
                "{Status} ({Error}), durationNs={Duration}",
                meeting.Id, egressId, status, payload.EgressInfo?.Error, fileResult?.DurationNanoseconds);
        }

        meeting.UpdatedUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(ct);

        if (completed && hasMedia)
        {
            _logger.LogInformation(
                "Meeting {MeetingId} recording saved: key={BlobKey}, durationSec={Duration}",
                meeting.Id, meeting.RecordingBlobKey, meeting.RecordingDurationSeconds);

            BackgroundJob.Enqueue<ITranscriptionJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));
        }
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

        // Not every participant is one of our users. The egress worker joins the room to record it,
        // with an identity like "EG_xxxxxxxx", and MeetingParticipant.UserId is a foreign key to
        // AspNetUsers — so inserting it violates the constraint, the handler throws, and LiveKit
        // sees a 500 and retries the same event on a backoff forever.
        //
        // Checking the user exists rather than filtering on an "EG_" prefix keeps this correct for
        // any other non-user identity LiveKit introduces later.
        var isKnownUser = await _dbContext.Users.AnyAsync(u => u.Id == userId, ct);
        if (!isKnownUser)
        {
            _logger.LogDebug(
                "Ignoring participant_joined for non-user identity {Identity} in room {RoomName} " +
                "(this is normally the recorder)",
                userId, payload.Room.Name);
            return;
        }

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