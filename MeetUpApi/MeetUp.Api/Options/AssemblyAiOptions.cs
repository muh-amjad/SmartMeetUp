namespace MeetUp.Api.Options;

public sealed class AssemblyAiOptions
{
    public const string SectionName = "AssemblyAi";

    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.assemblyai.com/v2";
}
