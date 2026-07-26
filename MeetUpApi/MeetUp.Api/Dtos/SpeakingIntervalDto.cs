namespace MeetUp.Api.Dtos;

/// <summary>
/// A completed speaking turn as observed by the client's LiveKit SDK. Wall-clock UTC is used
/// rather than an offset because the client has no reliable view of when the recording began —
/// the server rebases these against the meeting's actual start.
/// </summary>
public sealed class SpeakingIntervalDto
{
    public DateTime StartedUtc { get; set; }
    public DateTime StoppedUtc { get; set; }
}
