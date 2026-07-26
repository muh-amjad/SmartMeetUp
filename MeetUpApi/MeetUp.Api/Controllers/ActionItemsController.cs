using System.Security.Claims;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Infrastructure.Exceptions;
using MeetUp.Api.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeetUp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/action-items")]
public class ActionItemsController : ControllerBase
{
    private readonly IMeetingAnalysisRepository _analysisRepository;
    private readonly IMeetingParticipantRepository _participantRepository;

    public ActionItemsController(
        IMeetingAnalysisRepository analysisRepository,
        IMeetingParticipantRepository participantRepository)
    {
        _analysisRepository = analysisRepository;
        _participantRepository = participantRepository;
    }

    /// <summary>Toggle status, edit the description, reassign, or set/clear a due date.</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ActionItemDto>> Update(
        Guid id, [FromBody] UpdateActionItemRequestDto request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new ForbiddenException("User id claim missing.");

        var item = await _analysisRepository.GetActionItemAsync(id, ct)
            ?? throw new NotFoundException($"Action item {id} not found.");

        // Anyone in the meeting can tick items off — they are shared work, not host-only.
        var participants = await _participantRepository.GetByMeetingAsync(item.MeetingId, ct);
        var isHost = string.Equals(item.Meeting?.HostUserId, userId, StringComparison.Ordinal);
        if (!isHost && participants.All(p => p.UserId != userId))
        {
            throw new ForbiddenException("You are not part of this meeting.");
        }

        if (!string.IsNullOrWhiteSpace(request.Description))
        {
            item.Description = request.Description.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!Enum.TryParse<ActionItemStatus>(request.Status, ignoreCase: true, out var status))
            {
                throw new Infrastructure.Exceptions.ValidationException(
                    nameof(request.Status),
                    [$"Unknown status '{request.Status}'. Expected Open, Done or Cancelled."]);
            }

            item.Status = status;
            item.CompletedUtc = status == ActionItemStatus.Done ? DateTime.UtcNow : null;
        }

        if (request.AssigneeUserId is not null)
        {
            // Empty string means "unassign"; a value must belong to someone actually in the meeting.
            if (request.AssigneeUserId.Length == 0)
            {
                item.AssigneeUserId = null;
            }
            else if (participants.Any(p => p.UserId == request.AssigneeUserId))
            {
                item.AssigneeUserId = request.AssigneeUserId;
            }
            else
            {
                throw new Infrastructure.Exceptions.ValidationException(
                    nameof(request.AssigneeUserId),
                    ["The assignee must be a participant of this meeting."]);
            }
        }

        if (request.ClearDueDate)
        {
            item.DueDateUtc = null;
        }
        else if (request.DueDateUtc.HasValue)
        {
            item.DueDateUtc = request.DueDateUtc;
        }

        await _analysisRepository.SaveChangesAsync(ct);

        var updated = await _analysisRepository.GetActionItemAsync(id, ct)!;
        return Ok(MeetingsController.ToDto(updated!));
    }
}
