using System.Security.Claims;
using Serilog.Core;
using Serilog.Events;

namespace MeetUp.Api.Infrastructure.Logging;

public sealed class UserIdEnricher(IHttpContextAccessor httpContextAccessor) : ILogEventEnricher
{
    private const string UserIdPropertyName = "UserId";
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return;
        }

        var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.IsNullOrEmpty(userId))
        {
            var userIdProperty = propertyFactory.CreateProperty(UserIdPropertyName, userId);
            logEvent.AddPropertyIfAbsent(userIdProperty);
        }
    }
}
