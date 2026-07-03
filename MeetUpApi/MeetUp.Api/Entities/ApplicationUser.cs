using Microsoft.AspNetCore.Identity;

namespace MeetUp.Api.Entities
{
    public class ApplicationUser : IdentityUser
    {
        public string DisplayName { get; set; } = string.Empty;
    }
}
