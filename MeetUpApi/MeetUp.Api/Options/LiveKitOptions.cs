namespace MeetUp.Api.Options;

public sealed class LiveKitOptions
{
    public const string SectionName = "LiveKit";

    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string WsUrl { get; set; } = string.Empty;
    public string HttpUrl { get; set; } = string.Empty;
}