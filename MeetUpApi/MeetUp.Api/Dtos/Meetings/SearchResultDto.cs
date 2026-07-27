namespace MeetUp.Api.Dtos.Meetings;

public sealed class SearchResultDto
{
    public Guid MeetingId { get; set; }
    public string MeetingTitle { get; set; } = string.Empty;
    public DateTime MeetingDate { get; set; }
    public string Snippet { get; set; } = string.Empty;
    public int StartMs { get; set; }
    public double Score { get; set; }
}
