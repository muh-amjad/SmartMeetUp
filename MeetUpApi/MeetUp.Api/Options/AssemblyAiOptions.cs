namespace MeetUp.Api.Options;

public sealed class AssemblyAiOptions
{
    public const string SectionName = "AssemblyAi";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.assemblyai.com/v2";

    /// <summary>
    /// Upload each recording to AssemblyAI instead of handing it a presigned storage URL.
    /// AssemblyAI normally downloads the recording itself, which requires storage to be reachable
    /// from the internet. Local development stores recordings on 127.0.0.1, which it can never
    /// reach, so every transcription failed there. Uploading sends the bytes from the API instead.
    /// Off by default: on a deployment with public storage, letting AssemblyAI fetch directly
    /// avoids streaming every recording through the API.
    /// </summary>
    public bool UploadRecordings { get; set; }
}
