namespace MeetUp.Api.Services.Ai;

public interface IAnalysisProviderFactory
{
    /// <summary>The system default provider, or null when no provider has an API key configured.</summary>
    IAnalysisProvider? GetDefault();

    /// <summary>
    /// Picks a provider using the fallback chain: the meeting's frozen request, then the user's
    /// preference, then the system default. Returns null when nothing is configured.
    /// </summary>
    IAnalysisProvider? Resolve(string? requested, string? userPreference);
}
