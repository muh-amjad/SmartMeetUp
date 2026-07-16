namespace MeetUp.Api.Dtos.Meetings;

public sealed class ChatMessageDto
{
    public Guid Id { get; set; }
    public string SenderUserId { get; set; } = string.Empty;
    public string SenderUsername { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime SentUtc { get; set; }
}