using System.Security.Claims;
using Hangfire;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Infrastructure;
using MeetUp.Api.Infrastructure.Exceptions;
using MeetUp.Api.Jobs;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using MeetUp.Api.Services.Ai;
using MeetUp.Api.Services.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class MeetingsController : ControllerBase
{
    private readonly IMeetingRepository _meetingRepository;
    private readonly IMeetingParticipantRepository _participantRepository;
    private readonly IChatMessageRepository _chatMessageRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly IMeetingAnalysisRepository _analysisRepository;
    private readonly IMeetingAnalyticsRepository _analyticsRepository;
    private readonly ILiveKitService _liveKitService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly AnalysisProviderRegistry _providerRegistry;
    private readonly IEmailService _emailService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly LiveKitOptions _liveKitOptions;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
    IMeetingRepository meetingRepository,
    IMeetingParticipantRepository participantRepository,
    IChatMessageRepository chatMessageRepository,
    ITranscriptRepository transcriptRepository,
    IMeetingAnalysisRepository analysisRepository,
    IMeetingAnalyticsRepository analyticsRepository,
    ILiveKitService liveKitService,
    IBlobStorageService blobStorageService,
    AnalysisProviderRegistry providerRegistry,
    IEmailService emailService,
    UserManager<ApplicationUser> userManager,
    IOptions<LiveKitOptions> liveKitOptions,
    ILogger<MeetingsController> logger)
    {
        _userManager = userManager;
        _meetingRepository = meetingRepository;
        _participantRepository = participantRepository;
        _chatMessageRepository = chatMessageRepository;
        _transcriptRepository = transcriptRepository;
        _analysisRepository = analysisRepository;
        _analyticsRepository = analyticsRepository;
        _liveKitService = liveKitService;
        _blobStorageService = blobStorageService;
        _providerRegistry = providerRegistry;
        _emailService = emailService;
        _liveKitOptions = liveKitOptions.Value;
        _logger = logger;
    }

    /// <summary>Creates a new meeting and returns the host's LiveKit access token.</summary>
    [HttpPost]
    // A meeting is the entry point to every paid downstream step — recording, transcription,
    // analysis — so the daily cap here is what actually protects the free-tier quotas.
    [EnableRateLimiting(RateLimitPolicies.MeetingCreation)]
    public async Task<ActionResult<CreateMeetingResponseDto>> Create(
        [FromBody] CreateMeetingRequestDto? request,
        CancellationToken ct)
    {
        var hostUserId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");
        var hostUsername = User.Identity?.Name ?? "Host";

        // Freeze the host's provider choice now: changing the preference later must not retroactively
        // change how meetings that are already under way get analysed.
        var host = await _userManager.FindByIdAsync(hostUserId);

        var meeting = new Meeting
        {
            Id = Guid.NewGuid(),
            HostUserId = hostUserId,
            Title = string.IsNullOrWhiteSpace(request?.Title) ? "Untitled Meeting" : request!.Title.Trim(),
            LiveKitRoomName = $"meeting-{Guid.NewGuid():N}",  // unique room name in LiveKit
            Status = MeetingStatus.Scheduled,
            AnalysisProviderRequested = host?.PreferredAnalysisProviderKey,
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow,
        };

        await _meetingRepository.AddAsync(meeting, ct);
        await _meetingRepository.SaveChangesAsync(ct);

        // Same reasoning as in Join: membership is known here, so record it now instead of waiting
        // for the participant_joined webhook to tell us something we already know.
        await _participantRepository.AddAsync(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meeting.Id,
            UserId = hostUserId,
            Role = ParticipantRole.Host,
        }, ct);
        await _participantRepository.SaveChangesAsync(ct);

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

        // Kick off audio recording. Egress waits for the room to become active, so it's safe
        // to start this immediately even though no one has joined yet. Best-effort: a meeting
        // must still be usable even if the recording pipeline is unavailable.
        try
        {
            var outputKey = $"recordings/{meeting.Id}/{DateTime.UtcNow:yyyyMMddHHmmss}.ogg";
            meeting.EgressId = await _liveKitService.StartCompositeEgressAsync(meeting.LiveKitRoomName, outputKey, ct);
            await _meetingRepository.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to start egress for meeting {MeetingId}; meeting will not be recorded",
                meeting.Id);
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

        // Record membership here rather than waiting for LiveKit's participant_joined webhook.
        //
        // That webhook is asynchronous, and the client calls /chat immediately after joining — so
        // the read routinely overtakes the webhook and the endpoint's "are you a participant?" check
        // fails with 403 for anyone who is not the host. Membership is something this request
        // already knows for certain, so there is no reason to learn it from a round trip.
        //
        // The webhook still runs and fills in JoinedUtc; it just no longer gates access.
        var existing = await _participantRepository.GetByMeetingAndUserAsync(meeting.Id, userId, ct);
        if (existing is null)
        {
            await _participantRepository.AddAsync(new MeetingParticipant
            {
                Id = Guid.NewGuid(),
                MeetingId = meeting.Id,
                UserId = userId,
                Role = isHost ? ParticipantRole.Host : ParticipantRole.Participant,
            }, ct);
        }
        else
        {
            // Rejoining after having left.
            existing.LeftUtc = null;
        }

        await _participantRepository.SaveChangesAsync(ct);

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

    /// <summary>List the caller's meetings (as host or participant).</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<MeetingListItemDto>>> ListMeetings(
        [FromQuery] MeetingStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meetings = await _meetingRepository.GetUserMeetingsAsync(userId, status, page, pageSize, ct);

        var result = meetings.Select(m => new MeetingListItemDto
        {
            MeetingId = m.Id,
            Title = m.Title,
            Status = m.Status.ToString(),
            ScheduledStartUtc = m.ScheduledStartUtc,
            ActualStartUtc = m.ActualStartUtc,
            EndedUtc = m.EndedUtc,
            CreatedUtc = m.CreatedUtc,
            ParticipantCount = m.Participants?.Count ?? 0,
            IsHost = string.Equals(m.HostUserId, userId, StringComparison.Ordinal),
        }).ToList();

        return Ok(result);
    }

    /// <summary>Meeting detail with participants.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<MeetingDetailDto>> GetMeeting(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        // Access control: only host or participants can see the detail
        var participants = await _participantRepository.GetByMeetingAsync(id, ct);
        var isHost = string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal);
        var isParticipant = participants.Any(p => p.UserId == userId);

        if (!isHost && !isParticipant)
        {
            throw new ForbiddenException("You are not part of this meeting.");
        }

        return Ok(new MeetingDetailDto
        {
            MeetingId = meeting.Id,
            Title = meeting.Title,
            HostUserId = meeting.HostUserId,
            HostUsername = meeting.Host?.UserName ?? string.Empty,
            Status = meeting.Status.ToString(),
            ScheduledStartUtc = meeting.ScheduledStartUtc,
            ActualStartUtc = meeting.ActualStartUtc,
            EndedUtc = meeting.EndedUtc,
            LiveKitRoomName = meeting.LiveKitRoomName,
            RecordingDurationSeconds = meeting.RecordingDurationSeconds,
            CreatedUtc = meeting.CreatedUtc,
            IsHost = isHost,
            Participants = participants.Select(p => new ParticipantDto
            {
                UserId = p.UserId,
                Username = p.User?.UserName ?? string.Empty,
                Role = p.Role.ToString(),
                JoinedUtc = p.JoinedUtc,
                LeftUtc = p.LeftUtc,
                SpeakingSeconds = p.SpeakingSeconds,
            }).ToList(),
        });
    }

    /// <summary>Update meeting title / schedule (host only).</summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> UpdateMeeting(Guid id, [FromBody] UpdateMeetingRequestDto request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can update this meeting.");
        }

        if (!string.IsNullOrWhiteSpace(request.Title))
        {
            meeting.Title = request.Title.Trim();
        }
        if (request.ScheduledStartUtc.HasValue)
        {
            meeting.ScheduledStartUtc = request.ScheduledStartUtc;
        }

        meeting.UpdatedUtc = DateTime.UtcNow;
        await _meetingRepository.UpdateAsync(meeting, ct);
        await _meetingRepository.SaveChangesAsync(ct);

        return NoContent();
    }

    /// <summary>Delete a meeting (host only). Cascades to participants + chat messages.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteMeeting(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can delete this meeting.");
        }

        await _meetingRepository.DeleteAsync(meeting, ct);
        await _meetingRepository.SaveChangesAsync(ct);

        _logger.LogInformation("Meeting {MeetingId} deleted by host {UserId}", meeting.Id, userId);
        return NoContent();
    }

    /// <summary>Chat message history for a meeting.</summary>
    [HttpGet("{id:guid}/chat")]
    public async Task<ActionResult<IReadOnlyList<ChatMessageDto>>> GetChat(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        // Access control same as GetMeeting
        var participants = await _participantRepository.GetByMeetingAsync(id, ct);
        var isHost = string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal);
        var isParticipant = participants.Any(p => p.UserId == userId);
        if (!isHost && !isParticipant)
        {
            throw new ForbiddenException("You are not part of this meeting.");
        }

        var messages = await _chatMessageRepository.GetByMeetingAsync(id, ct);

        return Ok(messages.Select(m => new ChatMessageDto
        {
            Id = m.Id,
            SenderUserId = m.SenderUserId,
            SenderUsername = m.Sender?.UserName ?? string.Empty,
            Text = m.Text,
            SentUtc = m.SentUtc,
        }).ToList());
    }

    /// <summary>Diarized transcript for a meeting, once transcription has completed.</summary>
    [HttpGet("{id:guid}/transcript")]
    public async Task<ActionResult<TranscriptDto>> GetTranscript(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var transcript = await _transcriptRepository.GetByMeetingIdAsync(id, ct)
            ?? throw new NotFoundException("Transcript is not available for this meeting yet.");

        return Ok(new TranscriptDto
        {
            Language = transcript.Language,
            Utterances = transcript.Utterances
                .OrderBy(u => u.StartMs)
                .Select(u => new TranscriptUtteranceDto
                {
                    SpeakerLabel = u.SpeakerLabel,
                    ParticipantUserId = u.ParticipantUserId,
                    ParticipantUsername = u.Participant?.UserName,
                    StartMs = u.StartMs,
                    EndMs = u.EndMs,
                    Text = u.Text,
                    Confidence = u.Confidence,
                })
                .ToList(),
        });
    }

    /// <summary>Short-lived signed URL for playing back the meeting's recording.</summary>
    [HttpGet("{id:guid}/recording-url")]
    public async Task<ActionResult<RecordingUrlDto>> GetRecordingUrl(Guid id, CancellationToken ct)
    {
        var meeting = await EnsureCallerCanAccessMeetingAsync(id, ct);

        if (string.IsNullOrWhiteSpace(meeting.RecordingBlobKey))
        {
            throw new NotFoundException("No recording is available for this meeting.");
        }

        var expiry = TimeSpan.FromMinutes(5);
        var url = await _blobStorageService.GetSignedDownloadUrlAsync(meeting.RecordingBlobKey, expiry, ct);

        return Ok(new RecordingUrlDto
        {
            Url = url,
            ExpiresUtc = DateTime.UtcNow.Add(expiry),
        });
    }

    /// <summary>Host-only: re-enqueues transcription after a failed run.</summary>
    [HttpPost("{id:guid}/transcript/retry")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> RetryTranscript(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can retry transcription.");
        }

        if (meeting.Status != MeetingStatus.Failed)
        {
            throw new ConflictException("Transcription can only be retried when the meeting is in a Failed state.");
        }

        BackgroundJob.Enqueue<ITranscriptionJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));

        _logger.LogInformation("User {UserId} re-enqueued transcription for meeting {MeetingId}", userId, meeting.Id);
        return Accepted();
    }

    /// <summary>AI-generated overview and key topics.</summary>
    [HttpGet("{id:guid}/summary")]
    public async Task<ActionResult<MeetingSummaryDto>> GetSummary(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var summary = await _analysisRepository.GetSummaryAsync(id, ct)
            ?? throw new NotFoundException("This meeting has not been analysed yet.");

        return Ok(new MeetingSummaryDto
        {
            OverviewText = summary.OverviewText,
            KeyTopics = summary.KeyTopics,
            ProviderKey = summary.ProviderKey,
            ProviderDisplayName = _providerRegistry.Find(summary.ProviderKey)?.DisplayName ?? summary.ProviderKey,
            ModelUsed = summary.ModelUsed,
            GeneratedUtc = summary.GeneratedUtc,
        });
    }

    /// <summary>AI-extracted action items for a meeting.</summary>
    [HttpGet("{id:guid}/action-items")]
    public async Task<ActionResult<IReadOnlyList<ActionItemDto>>> GetActionItems(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var items = await _analysisRepository.GetActionItemsAsync(id, ct);
        return Ok(items.Select(ToDto).ToList());
    }

    /// <summary>AI-extracted decisions for a meeting.</summary>
    [HttpGet("{id:guid}/decisions")]
    public async Task<ActionResult<IReadOnlyList<DecisionDto>>> GetDecisions(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var decisions = await _analysisRepository.GetDecisionsAsync(id, ct);
        return Ok(decisions.Select(d => new DecisionDto
        {
            Id = d.Id,
            Description = d.Description,
            SourceStartMs = d.SourceUtterance?.StartMs,
            CreatedUtc = d.CreatedUtc,
        }).ToList());
    }

    /// <summary>The AI-drafted follow-up email (sending itself lands in Phase 9).</summary>
    [HttpGet("{id:guid}/follow-up-email")]
    public async Task<ActionResult<FollowUpEmailDto>> GetFollowUpEmail(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var email = await _analysisRepository.GetFollowUpEmailAsync(id, ct)
            ?? throw new NotFoundException("No follow-up email has been drafted for this meeting.");

        return Ok(new FollowUpEmailDto
        {
            Subject = email.Subject,
            BodyMarkdown = email.BodyMarkdown,
            Status = email.Status.ToString(),
            SentUtc = email.SentUtc,
            CreatedUtc = email.CreatedUtc,
        });
    }

    /// <summary>Host-only: edit the drafted follow-up email.</summary>
    [HttpPut("{id:guid}/follow-up-email")]
    public async Task<IActionResult> UpdateFollowUpEmail(
        Guid id, [FromBody] UpdateFollowUpEmailRequestDto request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can edit the follow-up email.");
        }

        var email = await _analysisRepository.GetFollowUpEmailAsync(id, ct)
            ?? throw new NotFoundException("No follow-up email has been drafted for this meeting.");

        if (email.Status == FollowUpEmailStatus.Sent)
        {
            throw new ConflictException("This email has already been sent and can no longer be edited.");
        }

        if (!string.IsNullOrWhiteSpace(request.Subject))
        {
            email.Subject = request.Subject.Trim();
        }

        email.BodyMarkdown = request.BodyMarkdown ?? string.Empty;
        email.EditedByUserId = userId;

        await _analysisRepository.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Host-only: re-runs AI analysis. Requires an existing transcript.</summary>
    [HttpPost("{id:guid}/analysis/retry")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> RetryAnalysis(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can retry analysis.");
        }

        var transcript = await _transcriptRepository.GetByMeetingIdAsync(id, ct)
            ?? throw new ConflictException("Analysis needs a transcript, and this meeting has not been transcribed.");

        BackgroundJob.Enqueue<IAiAnalysisJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));

        _logger.LogInformation("User {UserId} re-enqueued AI analysis for meeting {MeetingId}", userId, meeting.Id);
        return Accepted();
    }

    /// <summary>Speaking distribution and word counts for a meeting.</summary>
    [HttpGet("{id:guid}/analytics")]
    public async Task<ActionResult<MeetingAnalyticsDto>> GetAnalytics(Guid id, CancellationToken ct)
    {
        await EnsureCallerCanAccessMeetingAsync(id, ct);

        var analytics = await _analyticsRepository.GetAnalyticsAsync(id, ct)
            ?? throw new NotFoundException("Analytics have not been computed for this meeting yet.");

        return Ok(new MeetingAnalyticsDto
        {
            TotalDurationSeconds = analytics.TotalDurationSeconds,
            ParticipantCount = analytics.ParticipantCount,
            WordCount = analytics.WordCount,
            AverageWordsPerMinute = analytics.AverageWordsPerMinute,
            TotalSpeakingSeconds = analytics.SpeakingDistribution.Sum(s => s.Seconds),
            SpeakingDistribution = analytics.SpeakingDistribution
                .Select(s => new SpeakingShareDto
                {
                    UserId = s.UserId,
                    DisplayName = s.DisplayName,
                    Seconds = s.Seconds,
                    Percent = s.Percent,
                })
                .ToList(),
            ComputedUtc = analytics.ComputedUtc,
        });
    }

    /// <summary>Host-only: recompute speaker attribution and analytics for an existing transcript.</summary>
    [HttpPost("{id:guid}/analytics/recompute")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> RecomputeAnalytics(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can recompute analytics.");
        }

        _ = await _transcriptRepository.GetByMeetingIdAsync(id, ct)
            ?? throw new ConflictException("Analytics need a transcript, and this meeting has not been transcribed.");

        BackgroundJob.Enqueue<ISpeakerMappingJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));

        _logger.LogInformation("User {UserId} re-enqueued analytics for meeting {MeetingId}", userId, meeting.Id);
        return Accepted();
    }

    /// <summary>
    /// Who the follow-up email would reach. Lets the host confirm the list before sending, and
    /// reports whether the server can send at all.
    /// </summary>
    [HttpGet("{id:guid}/follow-up-email/recipients")]
    public async Task<ActionResult<FollowUpRecipientsDto>> GetFollowUpRecipients(Guid id, CancellationToken ct)
    {
        var meeting = await EnsureCallerCanAccessMeetingAsync(id, ct);
        var (recipients, optedOut) = await ResolveRecipientsAsync(meeting, ct);

        return Ok(new FollowUpRecipientsDto
        {
            Recipients = recipients
                .Select(r => new FollowUpRecipientDto
                {
                    UserId = r.UserId,
                    Email = r.Email,
                    DisplayName = r.DisplayName,
                })
                .ToList(),
            OptedOutCount = optedOut,
            CanSend = _emailService.IsConfigured,
        });
    }

    /// <summary>Host-only: sends the drafted follow-up email to everyone who attended.</summary>
    [HttpPost("{id:guid}/follow-up-email/send")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> SendFollowUpEmail(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can send the follow-up email.");
        }

        if (!_emailService.IsConfigured)
        {
            throw new ConflictException(
                "No email transport is configured on the server, so the follow-up cannot be sent.");
        }

        var email = await _analysisRepository.GetFollowUpEmailAsync(id, ct)
            ?? throw new NotFoundException("No follow-up email has been drafted for this meeting.");

        // Sending twice would spam everyone, so Sent is a terminal state.
        if (email.Status == FollowUpEmailStatus.Sent)
        {
            throw new ConflictException($"This email was already sent on {email.SentUtc:u}.");
        }

        var (recipients, _) = await ResolveRecipientsAsync(meeting, ct);
        if (recipients.Count == 0)
        {
            throw new ConflictException(
                "There is nobody to send to — every participant has opted out or has no email address.");
        }

        var html = FollowUpEmailRenderer.Render(email.Subject, email.BodyMarkdown, meeting.Title);

        // Send first: if the provider rejects it, the draft must stay sendable rather than being
        // marked Sent with nothing delivered.
        await _emailService.SendAsync(
            recipients.Select(r => r.Email).ToList(), email.Subject, html, ct);

        var sentAt = DateTime.UtcNow;
        email.Status = FollowUpEmailStatus.Sent;
        email.SentUtc = sentAt;

        await _analysisRepository.AddFollowUpRecipientsAsync(
            recipients.Select(r => new FollowUpEmailRecipient
            {
                Id = Guid.NewGuid(),
                FollowUpEmailId = email.Id,
                RecipientEmail = r.Email,
                RecipientUserId = r.UserId,
                SentAtUtc = sentAt,
            }),
            ct);

        await _analysisRepository.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Follow-up email for meeting {MeetingId} sent to {RecipientCount} recipient(s) via {Transport}",
            meeting.Id, recipients.Count, _emailService.TransportName);

        return NoContent();
    }

    /// <summary>
    /// Everyone who attended, plus the host, minus opt-outs and anyone without an address.
    /// Deduplicated by email so one person listed twice is only mailed once.
    /// </summary>
    private async Task<(List<(string UserId, string Email, string DisplayName)> Recipients, int OptedOut)>
        ResolveRecipientsAsync(Meeting meeting, CancellationToken ct)
    {
        var participants = await _participantRepository.GetByMeetingAsync(meeting.Id, ct);

        var candidates = participants
            .Select(p => p.User)
            .Where(u => u is not null)
            .ToList();

        // The host counts as a recipient even if no participant row was ever written for them
        // (for example a meeting that ended before anyone else joined).
        if (candidates.All(u => u!.Id != meeting.HostUserId))
        {
            var host = await _userManager.FindByIdAsync(meeting.HostUserId);
            if (host is not null)
            {
                candidates.Add(host);
            }
        }

        var optedOut = candidates.Count(u => u!.OptOutFollowUpEmails);

        var recipients = candidates
            .Where(u => !u!.OptOutFollowUpEmails && !string.IsNullOrWhiteSpace(u.Email))
            .GroupBy(u => u!.Email!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var user = group.First()!;
                return (
                    UserId: user.Id,
                    Email: user.Email!,
                    DisplayName: user.DisplayName is { Length: > 0 } name ? name : user.UserName ?? user.Email!);
            })
            .ToList();

        return (recipients, optedOut);
    }

    /// <summary>
    /// Host-only: rebuilds this meeting's search index. Needed because indexing otherwise only
    /// happens once, right after transcription — without this there is no way to recover a failed
    /// index or to make a meeting transcribed before search existed searchable.
    /// </summary>
    [HttpPost("{id:guid}/search-index/rebuild")]
    [EnableRateLimiting(RateLimitPolicies.Expensive)]
    public async Task<IActionResult> RebuildSearchIndex(Guid id, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Meeting {id} not found.");

        if (!string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal))
        {
            throw new ForbiddenException("Only the host can rebuild the search index.");
        }

        _ = await _transcriptRepository.GetByMeetingIdAsync(id, ct)
            ?? throw new ConflictException("Indexing needs a transcript, and this meeting has not been transcribed.");

        BackgroundJob.Enqueue<IEmbeddingJob>(j => j.RunAsync(meeting.Id, CancellationToken.None));

        _logger.LogInformation("User {UserId} re-enqueued search indexing for meeting {MeetingId}", userId, meeting.Id);
        return Accepted();
    }

    internal static ActionItemDto ToDto(ActionItem item) => new()
    {
        Id = item.Id,
        MeetingId = item.MeetingId,
        Description = item.Description,
        AssigneeUserId = item.AssigneeUserId,
        AssigneeUsername = item.Assignee?.DisplayName is { Length: > 0 } name ? name : item.Assignee?.UserName,
        AssigneeNameRaw = item.AssigneeNameRaw,
        DueDateUtc = item.DueDateUtc,
        Status = item.Status.ToString(),
        CompletedUtc = item.CompletedUtc,
        SourceStartMs = item.SourceUtterance?.StartMs,
    };

    /// <summary>Shared host-or-participant access check used by the transcript/recording endpoints.</summary>
    private async Task<Meeting> EnsureCallerCanAccessMeetingAsync(Guid meetingId, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct)
            ?? throw new NotFoundException($"Meeting {meetingId} not found.");

        var participants = await _participantRepository.GetByMeetingAsync(meetingId, ct);
        var isHost = string.Equals(meeting.HostUserId, userId, StringComparison.Ordinal);
        var isParticipant = participants.Any(p => p.UserId == userId);

        if (!isHost && !isParticipant)
        {
            throw new ForbiddenException("You are not part of this meeting.");
        }

        return meeting;
    }
}