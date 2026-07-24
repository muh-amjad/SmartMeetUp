using Hangfire.Dashboard;

namespace MeetUp.Api.Infrastructure;

/// <summary>Restricts the /hangfire dashboard to authenticated users in the "Admin" role.</summary>
public sealed class HangfireAdminAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        return httpContext.User.Identity?.IsAuthenticated == true
            && httpContext.User.IsInRole("Admin");
    }
}
