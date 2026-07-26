namespace MeetUp.Api.Services.AssemblyAi;

public interface IAssemblyAiClient
{
    /// <summary>Submits a publicly-reachable audio URL for transcription. Returns the AssemblyAI transcript id.</summary>
    Task<string> SubmitTranscriptionAsync(string audioUrl, CancellationToken ct);

    Task<AssemblyAiTranscriptResult> GetTranscriptAsync(string transcriptId, CancellationToken ct);
}
