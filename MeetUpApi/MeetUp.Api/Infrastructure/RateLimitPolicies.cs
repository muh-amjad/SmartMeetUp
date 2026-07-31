using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using MeetUp.Api.Options;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Infrastructure;

/// <summary>
/// Named rate-limit policies. Uses the framework's built-in limiter rather than a package, and
/// partitions per user where a user exists so one noisy account cannot exhaust everyone's budget —
/// falling back to the client IP only for the endpoints that run before sign-in.
///
/// Every policy reads its ceiling from options *per request*. Reading configuration eagerly while
/// registering services looks equivalent but is not: sources layered in after that point — notably a
/// test host's own configuration — would be invisible, and the app would silently run on defaults.
/// </summary>
public static class RateLimitPolicies
{
    public const string Auth = "auth";
    public const string General = "general";
    public const string Expensive = "expensive";
    public const string MeetingCreation = "meeting-creation";

    public static void AddPolicies(RateLimiterOptions limiter)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // Tell the caller when to come back instead of leaving them to guess.
        limiter.OnRejected = async (context, ct) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
            }

            context.HttpContext.Response.ContentType = "application/problem+json";
            await context.HttpContext.Response.WriteAsync(
                """{"title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Please slow down and try again."}""",
                ct);
        };

        // Sign-in traffic is unauthenticated by definition, so it can only be partitioned by IP.
        limiter.AddPolicy(Auth, context => Window(
            context, ClientIp(context), limits => limits.AuthPerMinute, TimeSpan.FromMinutes(1)));

        limiter.AddPolicy(General, context => Window(
            context, UserOrIp(context), limits => limits.GeneralPerMinute, TimeSpan.FromMinutes(1)));

        limiter.AddPolicy(Expensive, context => Window(
            context, UserOrIp(context), limits => limits.ExpensivePerMinute, TimeSpan.FromMinutes(1)));

        // A daily window rather than per-minute: the point is to cap total spend on free AI and
        // transcription quotas, not to smooth out bursts.
        limiter.AddPolicy(MeetingCreation, context => Window(
            context, UserOrIp(context), limits => limits.MeetingsPerDay, TimeSpan.FromDays(1)));
    }

    private static RateLimitPartition<string> Window(
        HttpContext context,
        string partitionKey,
        Func<RateLimitOptions, int> permitLimit,
        TimeSpan window)
    {
        var limits = context.RequestServices
            .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
            .CurrentValue
            .RateLimits;

        // Switched off entirely: still routed through the limiter, but with nothing to hit.
        if (!limits.Enabled)
        {
            return RateLimitPartition.GetNoLimiter(partitionKey);
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, permitLimit(limits)),
                Window = window,
            });
    }

    private static string UserOrIp(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? $"user:{userId}"
            : $"ip:{ClientIp(context)}";

    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
