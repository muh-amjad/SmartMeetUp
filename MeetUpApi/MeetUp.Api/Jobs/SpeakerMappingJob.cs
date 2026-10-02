using MeetUp.Api.Entities;
using MeetUp.Api.Repositories;

namespace MeetUp.Api.Jobs;

/// <summary>
/// Attributes the transcript's anonymous speaker labels ("A", "B", ...) to real accounts by
/// overlapping each label's utterances with the speaking intervals the clients reported, then
/// computes per-meeting speaking analytics.
/// </summary>
public sealed class SpeakerMappingJob : ISpeakerMappingJob
{
    private readonly IMeetingRepository _meetingRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly IMeetingParticipantRepository _participantRepository;
    private readonly IMeetingAnalyticsRepository _analyticsRepository;
    private readonly ILogger<SpeakerMappingJob> _logger;

    public SpeakerMappingJob(
        IMeetingRepository meetingRepository,
        ITranscriptRepository transcriptRepository,
        IMeetingParticipantRepository participantRepository,
        IMeetingAnalyticsRepository analyticsRepository,
        ILogger<SpeakerMappingJob> logger)
    {
        _meetingRepository = meetingRepository;
        _transcriptRepository = transcriptRepository;
        _participantRepository = participantRepository;
        _analyticsRepository = analyticsRepository;
        _logger = logger;
    }

    public async Task RunAsync(Guid meetingId, CancellationToken ct)
    {
        var meeting = await _meetingRepository.GetByIdAsync(meetingId, ct);
        if (meeting is null)
        {
            _logger.LogWarning("SpeakerMappingJob: meeting {MeetingId} not found", meetingId);
            return;
        }

        var transcript = await _transcriptRepository.GetByMeetingIdAsync(meetingId, ct);
        if (transcript is null || transcript.Utterances.Count == 0)
        {
            _logger.LogWarning("SpeakerMappingJob: meeting {MeetingId} has no transcript utterances", meetingId);
            return;
        }

        var participants = await _participantRepository.GetByMeetingAsync(meetingId, ct);
        var activity = await _analyticsRepository.GetAudioActivityAsync(meetingId, ct);

        var utterances = transcript.Utterances.ToList();

        // Attribution needs both sides: labelled utterances and per-user speaking intervals.
        // Without the intervals we can still report totals, just not who said what.
        if (activity.Count > 0)
        {
            var resolved = AssignLabelsToUsers(utterances, AlignToRecording(meeting, activity), participants);

            foreach (var utterance in utterances)
            {
                if (resolved.TryGetValue(utterance.SpeakerLabel, out var userId))
                {
                    utterance.ParticipantUserId = userId;
                }
            }

            var attributed = utterances.Count(u => u.ParticipantUserId is not null);
            _logger.LogInformation(
                "Meeting {MeetingId}: mapped {LabelCount} speaker label(s), attributing {Attributed}/{Total} utterances",
                meetingId, resolved.Count, attributed, utterances.Count);
        }
        else
        {
            _logger.LogWarning(
                "Meeting {MeetingId}: no speaking intervals were reported, so transcript speakers stay anonymous",
                meetingId);
        }

        UpdateParticipantSpeakingSeconds(participants, utterances);
        var analytics = BuildAnalytics(meeting, participants, utterances, transcript.FullText);

        await _analyticsRepository.UpsertAnalyticsAsync(analytics, ct);

        // Utterances, participants and analytics all hang off the same DbContext, so one save
        // commits the attribution and the computed totals together.
        await _analyticsRepository.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Moves the reported speaking intervals onto the transcript's timeline.
    ///
    /// Intervals are stored relative to the meeting's start (see MeetingHub.ReportSpeakingIntervals),
    /// but transcript timestamps count from the first frame of the recording. Those are not the same
    /// moment: the room is created before anyone joins, and the recorder only starts once media is
    /// flowing, so the recording begins seconds later. Left uncorrected, every interval sits that
    /// many seconds late against the utterances — enough, with short alternating turns, to match a
    /// label to the wrong person or to nobody, leaving it as "Speaker B".
    ///
    /// Meetings recorded before the recording start was captured are returned unchanged.
    /// </summary>
    private static IReadOnlyList<ParticipantAudioActivity> AlignToRecording(
        Meeting meeting,
        IReadOnlyList<ParticipantAudioActivity> activity)
    {
        if (meeting.RecordingStartedUtc is not { } recordingStarted)
        {
            return activity;
        }

        var origin = meeting.ActualStartUtc ?? meeting.CreatedUtc;
        var offsetMs = (int)(recordingStarted - origin).TotalMilliseconds;
        if (offsetMs == 0)
        {
            return activity;
        }

        // Copies, not the tracked entities: shifting those in place would persist the shifted
        // values on the save below and double-shift them if the job is ever re-run.
        return activity
            .Select(a => new ParticipantAudioActivity
            {
                Id = a.Id,
                MeetingId = a.MeetingId,
                UserId = a.UserId,
                StartedSpeakingMs = a.StartedSpeakingMs - offsetMs,
                StoppedSpeakingMs = a.StoppedSpeakingMs - offsetMs,
            })
            .ToList();
    }

    /// <summary>
    /// For each speaker label, picks the participant whose reported speaking time overlaps that
    /// label's utterances the most. A label with no overlap at all is left unassigned rather than
    /// guessed at, and two labels never collapse onto the same person — with one exception: when
    /// exactly one label and exactly one participant are left over, they can only belong together.
    /// </summary>
    private static Dictionary<string, string> AssignLabelsToUsers(
        IReadOnlyList<TranscriptUtterance> utterances,
        IReadOnlyList<ParticipantAudioActivity> activity,
        IReadOnlyList<MeetingParticipant> participants)
    {
        var activityByUser = activity
            .GroupBy(a => a.UserId)
            .ToDictionary(g => g.Key, g => g.Select(a => (a.StartedSpeakingMs, a.StoppedSpeakingMs)).ToList());

        var labels = utterances
            .Select(u => u.SpeakerLabel)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Score every (label, user) pair, then greedily take the strongest pairings first so that
        // one dominant speaker can't claim a label that fits another user better.
        var scores = new List<(string Label, string UserId, long Overlap)>();

        foreach (var label in labels)
        {
            var labelIntervals = utterances
                .Where(u => string.Equals(u.SpeakerLabel, label, StringComparison.OrdinalIgnoreCase))
                .Select(u => (u.StartMs, u.EndMs))
                .ToList();

            foreach (var (userId, userIntervals) in activityByUser)
            {
                var overlap = TotalOverlapMs(labelIntervals, userIntervals);
                if (overlap > 0)
                {
                    scores.Add((label, userId, overlap));
                }
            }
        }

        var assignment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var takenUsers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (label, userId, _) in scores.OrderByDescending(s => s.Overlap))
        {
            if (assignment.ContainsKey(label) || takenUsers.Contains(userId))
            {
                continue;
            }

            assignment[label] = userId;
            takenUsers.Add(userId);
        }

        // Resolve by elimination. A participant's intervals can be missing entirely (their client
        // never reported, or reported with a skewed clock), which leaves their label with no overlap.
        // In a 1:1 call that is the common case, and the answer is not in doubt.
        var unassignedLabels = labels.Where(l => !assignment.ContainsKey(l)).ToList();
        var unassignedUsers = participants
            .Select(p => p.UserId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => !takenUsers.Contains(id))
            .ToList();

        if (unassignedLabels.Count == 1 && unassignedUsers.Count == 1)
        {
            assignment[unassignedLabels[0]] = unassignedUsers[0];
        }

        return assignment;
    }

    /// <summary>Total milliseconds where any utterance interval intersects any speaking interval.</summary>
    private static long TotalOverlapMs(
        IReadOnlyList<(int Start, int End)> a,
        IReadOnlyList<(int Start, int End)> b)
    {
        long total = 0;

        foreach (var (aStart, aEnd) in a)
        {
            foreach (var (bStart, bEnd) in b)
            {
                var overlap = Math.Min(aEnd, bEnd) - Math.Max(aStart, bStart);
                if (overlap > 0)
                {
                    total += overlap;
                }
            }
        }

        return total;
    }

    /// <summary>
    /// Speaking time comes from the transcript rather than the reported intervals: the transcript
    /// only counts time that produced actual speech, whereas the active-speaker signal also fires
    /// on background noise.
    /// </summary>
    private static void UpdateParticipantSpeakingSeconds(
        IReadOnlyList<MeetingParticipant> participants,
        IReadOnlyList<TranscriptUtterance> utterances)
    {
        var msByUser = utterances
            .Where(u => u.ParticipantUserId is not null)
            .GroupBy(u => u.ParticipantUserId!)
            .ToDictionary(g => g.Key, g => g.Sum(u => (long)Math.Max(0, u.EndMs - u.StartMs)));

        foreach (var participant in participants)
        {
            participant.SpeakingSeconds = msByUser.TryGetValue(participant.UserId, out var ms)
                ? (int)(ms / 1000)
                : 0;
        }
    }

    private static MeetingAnalytics BuildAnalytics(
        Meeting meeting,
        IReadOnlyList<MeetingParticipant> participants,
        IReadOnlyList<TranscriptUtterance> utterances,
        string fullText)
    {
        // Prefer the recording's own length; fall back to the wall-clock span, then to the
        // transcript's last timestamp so a duration is always reported.
        int? wallClockSeconds = meeting.EndedUtc.HasValue && meeting.ActualStartUtc.HasValue
            ? (int)(meeting.EndedUtc.Value - meeting.ActualStartUtc.Value).TotalSeconds
            : null;

        var durationSeconds = meeting.RecordingDurationSeconds
            ?? wallClockSeconds
            ?? (utterances.Count > 0 ? utterances.Max(u => u.EndMs) / 1000 : 0);

        var totalSpeakingSeconds = participants.Sum(p => p.SpeakingSeconds);

        var distribution = participants
            .Select(p => new SpeakingShare
            {
                UserId = p.UserId,
                DisplayName = p.User?.DisplayName is { Length: > 0 } name ? name : p.User?.UserName ?? "Unknown",
                Seconds = p.SpeakingSeconds,
                Percent = totalSpeakingSeconds > 0
                    ? Math.Round(p.SpeakingSeconds * 100.0 / totalSpeakingSeconds, 1)
                    : 0,
            })
            .OrderByDescending(s => s.Seconds)
            .ToList();

        var wordCount = CountWords(fullText);

        return new MeetingAnalytics
        {
            MeetingId = meeting.Id,
            TotalDurationSeconds = Math.Max(0, durationSeconds),
            ParticipantCount = participants.Count,
            SpeakingDistribution = distribution,
            WordCount = wordCount,
            AverageWordsPerMinute = durationSeconds > 0
                ? Math.Round(wordCount / (durationSeconds / 60.0), 1)
                : 0,
            ComputedUtc = DateTime.UtcNow,
        };
    }

    private static int CountWords(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
}
