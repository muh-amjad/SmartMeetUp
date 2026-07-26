namespace MeetUp.Api.Dtos.Meetings;

public sealed class TranscriptDto
{
    public string Language { get; set; } = "en";
    public IReadOnlyList<TranscriptUtteranceDto> Utterances { get; set; } = Array.Empty<TranscriptUtteranceDto>();
}
