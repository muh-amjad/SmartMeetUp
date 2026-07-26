namespace MeetUp.Api.Entities;

public class Transcript
{
    public Guid Id { get; set; }

    public Guid MeetingId { get; set; }
    public Meeting? Meeting { get; set; }

    public string Language { get; set; } = "en";

    /// <summary>Concatenated plain text — used for full-text search once Phase 7 adds it.</summary>
    public string FullText { get; set; } = string.Empty;

    public string AssemblyAiTranscriptId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public ICollection<TranscriptUtterance> Utterances { get; set; } = new List<TranscriptUtterance>();
}
