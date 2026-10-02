namespace MeetUp.Api.Services.AssemblyAi;

public interface IAssemblyAiClient
{
    /// <summary>Submits a publicly-reachable audio URL for transcription. Returns the AssemblyAI transcript id.</summary>
    Task<string> SubmitTranscriptionAsync(string audioUrl, CancellationToken ct);

    /// <summary>
    /// Uploads a media file to AssemblyAI's own storage and returns the private URL to pass to
    /// <see cref="SubmitTranscriptionAsync"/>. For recordings AssemblyAI cannot download itself.
    /// </summary>
    Task<string> UploadAsync(Stream media, CancellationToken ct);

    Task<AssemblyAiTranscriptResult> GetTranscriptAsync(string transcriptId, CancellationToken ct);
}
