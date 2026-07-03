namespace MeetUp.Api.Dtos
{
    public class IceCandidateDto
    {
        public string Candidate { get; set; } = string.Empty;       // candidate string
        public string SdpMid { get; set; } = string.Empty;          // media stream id
        public int? SdpMLineIndex { get; set; }     // media line index
    }
}
