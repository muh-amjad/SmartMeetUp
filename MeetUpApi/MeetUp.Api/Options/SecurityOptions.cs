namespace MeetUp.Api.Options;

public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// Origins allowed to call the API. In production this is the single site origin; locally it is
    /// the Angular dev server. Configured rather than hardcoded so a deployment does not need a
    /// code change to serve a different domain.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = ["http://localhost:4200"];

    /// <summary>
    /// Adds HSTS. Only meaningful once TLS actually terminates in front of the app, so it stays off
    /// by default — sending it over plain HTTP in dev would pin the browser to https://localhost.
    /// </summary>
    public bool EnableHsts { get; set; }

    public RateLimitOptions RateLimits { get; set; } = new();
}

/// <summary>
/// Request ceilings. Values are configurable so the integration suite can raise them out of the way
/// and one dedicated test can lower them to prove the limiter actually rejects.
/// </summary>
public sealed class RateLimitOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Login/signup/refresh, per client IP — the endpoints worth brute-forcing.</summary>
    public int AuthPerMinute { get; set; } = 10;

    /// <summary>Everything else, per authenticated user.</summary>
    public int GeneralPerMinute { get; set; } = 100;

    /// <summary>Endpoints that spend AI or email quota, per user.</summary>
    public int ExpensivePerMinute { get; set; } = 5;

    /// <summary>Meetings a single user may create per day; protects free provider quotas from abuse.</summary>
    public int MeetingsPerDay { get; set; } = 10;
}
