using Microsoft.AspNetCore.Identity;

namespace MeetUp.Api.Entities
{
    public class ApplicationUser : IdentityUser
    {
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Preferred AI analysis provider key; null falls back to the system default.</summary>
        public string? PreferredAnalysisProviderKey { get; set; }
    }
}
