namespace MeetUp.Api.Options;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Envelope sender. Must be on a domain the provider has verified.</summary>
    public string FromEmail { get; set; } = string.Empty;

    public string FromName { get; set; } = "SmartMeetUp";

    public ResendOptions Resend { get; set; } = new();

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class ResendOptions
{
    /// <summary>Empty means Resend is not in use; the SMTP transport is then considered.</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Plain SMTP, intended for a local mail catcher during development so drafts can be inspected
/// without a provider account or a verified domain.
/// </summary>
public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 1025;
}
