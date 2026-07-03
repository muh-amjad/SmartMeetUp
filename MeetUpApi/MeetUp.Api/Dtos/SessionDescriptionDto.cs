namespace MeetUp.Api.Dtos
{
    public class SessionDescriptionDto
    {
        public string Type { get; set; } = string.Empty;     // "offer" or "answer"
        public string Sdp { get; set; } = string.Empty;      // SDP string from browser
    }
}
