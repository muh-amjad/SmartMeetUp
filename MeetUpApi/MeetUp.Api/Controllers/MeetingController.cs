using System.Security.Claims;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Infrastructure.Exceptions;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class MeetingsController : ControllerBase
{
    private readonly IMeetingRepository _meetingRepository;
    private readonly ILiveKitService _liveKitService;
    private readonly LiveKitOptions _liveKitOptions;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
        IMeetingRepository meetingRepository,
        ILiveKitService liveKitService,
        IOptions<LiveKitOptions> liveKitOptions,
        ILogger<MeetingsController> logger)
    {
        _meetingRepository = meetingRepository;
        _liveKitService = liveKitService;
        _liveKitOptions = liveKitOptions.Value;
        _logger = logger;
    }

    /// <summary>Creates a new meeting and returns the host's LiveKit access token.</summary>
    [HttpPost]
    public async Task<ActionResult<CreateMeetingResponseDto>> Create(
        [FromBody] CreateMeetingRequestDto? request,
        CancellationToken ct)
    {
        var hostUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");
        var hostUsername = User.Identity?.Name ?? "Host";

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            HostUserId = hostUserId,
            Title = string.IsNullOrWhiteSpace(request?.Title) ? "Untitled Meeting" : request!.Title.Trim(),
            LiveKitRoomName = $"meeting-{Guid.NewGuid():N}",  // unique room name in LiveKit
            Status = MeetingStatus.Scheduled,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        await _meetingRepository.AddAsync(meeting, ct);
        await _meetingRepository.SaveChangesAsync(ct);

        // Try to create the room upfront so we can control settings.
        // If this fails, LiveKit will auto-create on first participant join (room.auto_create=true).
        try
        {
            await _liveKitService.CreateRoomAsync(meeting.LiveKitRoomName, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "LiveKit CreateRoom failed for {RoomName}; relying on auto_create",
                meeting.LiveKitRoomName);
        }

        var token = _liveKitService.GenerateAccessToken(
            roomName: meeting.LiveKitRoomName,
            participantIdentity: hostUserId,
            displayName: hostUsername,
            isHost: true);

        _logger.LogInformation("User {UserId} created meeting {MeetingId} in room {RoomName}",
            hostUserId, meeting.Id, meeting.LiveKitRoomName);

        return Ok(new CreateMeetingResponseDto
        {
            MeetingId = meeting.Id,
            Title = meeting.Title,
            LivekitToken = token,
            LivekitWsUrl = _liveKitOptions.WsUrl,
            RoomName = meeting.LiveKitRoomName,
        });
    }

    /// <summary>Returns a LiveKit access token so the caller can join an existing meeting.</summary>
    [HttpPost("{id:guid}/join")]
    public async Task<ActionResult<JoinMeetingResponseDto>> Join(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");
        var username = User.Identity?.Name ?? "User";

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (meeting.Status == MeetingStatus.Ended || meeting.EndedUtc.HasValue)
        {
            throw new ConflictException("Meeting has already ended.");
        }

        var isHost = string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal);

        var token = _liveKitService.GenerateAccessToken(
            roomName: meeting.LiveKitRoomName,
            participantIdentity: userId,
            displayName: username,
            isHost: isHost);

        _logger.LogInformation("User {UserId} joining meeting {MeetingId} (host={IsHost})",
            userId, meeting.Id, isHost);

        return Ok(new JoinMeetingResponseDto
        {
            MeetingId = meeting.Id,
            Title = meeting.Title,
            LivekitToken = token,
            LivekitWsUrl = _liveKitOptions.WsUrl,
            RoomName = meeting.LiveKitRoomName,
            IsHost = isHost,
        });
    }

    /// <summary>Host-only: closes the LiveKit room and marks the meeting as ended.</summary>
    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> End(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can end this meeting.");
        }

        if (meeting.Status == MeetingStatus.Ended)
        {
            return NoContent();  // already ended, idempotent
        }

        await _liveKitService.EndRoomAsync(meeting.LiveKitRoomName, ct);

        meeting.Status = MeetingStatus.Ended;
        meeting.EndedUtc = DateTime.UtcNow;
        meeting.UpdatedUtc = DateTime.UtcNow;
        await _meetingRepository.UpdateAsync(meeting, ct);
        await _meetingRepository.SaveChangesAsync(ct);

        _logger.LogInformation("Meeting {MeetingId} ended by host {UserId}", meeting.Id, userId);
        return NoContent();
    }
}