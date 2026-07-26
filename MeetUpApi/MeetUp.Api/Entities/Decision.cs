namespace MeetUp.Api.Entities;

public class Decision
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string Description { get; set; } = string.Empty;

    public Guid? SourceUtteranceId { get; set; }
    public TranscriptUtterance? SourceUtterance { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
