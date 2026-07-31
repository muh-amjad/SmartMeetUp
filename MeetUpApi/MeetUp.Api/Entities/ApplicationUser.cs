using Microsoft.AspNetCore.Identity;

namespace MeetUp.Api.Entities
{
    public class ApplicationUser : IdentityUser
    {
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Preferred AI analysis provider key; null falls back to the system default.</summary>
        public string? PreferredAnalysisProviderKey { get; set; }

        /// <summary>When true, this user is excluded from follow-up emails for meetings they attend.</summary>
        public bool OptOutFollowUpEmails { get; set; }
    }
}
