namespace MeetUp.Api.Options;

/// <summary>
/// Emails that should be promoted to the "Admin" role at startup. This is the only way to get
/// an Admin today — there is no in-app promotion UI/endpoint yet (deliberately out of scope for
/// the recording pipeline; it just needs *someone* to be able to open /hangfire).
/// </summary>
public sealed class AdminBootstrapOptions
{
    public const string SectionName = "AdminBootstrap";

    public List<string> Emails { get; set; } = new();
}
