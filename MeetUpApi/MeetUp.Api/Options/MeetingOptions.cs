namespace MeetUp.Api.Options;

public sealed class MeetingOptions
{
    public const string Section = "Meeting";
    public int MaxUsersPerRoom { get; set; } = 5;
}
