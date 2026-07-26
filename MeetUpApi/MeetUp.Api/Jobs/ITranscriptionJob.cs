namespace MeetUp.Api.Jobs;

public interface ITranscriptionJob
{
    Task RunAsync(Guid meetingId, CancellationToken ct);
}
