namespace MeetUp.Api.Dtos.Users
{
    public class UserSearchResultDto
    {
        public string UserId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
        public string? ConnectionId { get; set; }
    }
}
