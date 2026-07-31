using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Infrastructure.Middleware;

/// <summary>
/// Adds the response headers a browser needs to refuse obvious attacks. Written as middleware rather
/// than left to the reverse proxy so the guarantees travel with the app — a misconfigured or replaced
/// proxy cannot silently drop them.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecurityOptions _options;

    public SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;

        // Stop browsers guessing a different content type than we declared.
        headers["X-Content-Type-Options"] = "nosniff";

        // This host only ever serves API responses, so nothing here should be framed at all.
        headers["X-Frame-Options"] = "DENY";

        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

        // No browser feature is needed by API responses.
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        if (_options.EnableHsts)
        {
            headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
        }

        await _next(context);
    }
}
