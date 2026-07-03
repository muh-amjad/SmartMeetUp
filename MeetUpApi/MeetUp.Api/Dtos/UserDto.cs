namespace MeetUp.Api.Dtos
{
    public class UserDto
    {
        public string AppUserId { get; set; }
        public string Email { get; set; }
        public string Username { get; set; }
        public string Id { get; set; }
        public bool IsInCall { get; set; }
        public string? RoomId { get; set; }

        public UserDto(string id, string appUserId, string username, string email)
        {
            this.Username = username;
            this.Id = id;
            this.AppUserId = appUserId;
            this.Email = email;
            this.IsInCall = false;
            this.RoomId = null;
        }

        public UserDto(string id, string appUserId, string username, string email, bool isInCall, string? roomId)
        {
            this.Username = username;
            this.Id = id;
            this.AppUserId = appUserId;
            this.Email = email;
            this.IsInCall = isInCall;
            this.RoomId = roomId;
        }
    }
}
