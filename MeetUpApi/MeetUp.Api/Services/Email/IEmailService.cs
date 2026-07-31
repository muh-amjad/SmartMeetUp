namespace MeetUp.Api.Services.Email;

/// <summary>
/// Deliberately provider-agnostic: swapping Resend for SendGrid, SES or plain SMTP is a
/// registration change, since every transport takes the same rendered HTML.
/// </summary>
public interface IEmailService
{
    /// <summary>False when no transport is configured, so callers can refuse before composing.</summary>
    bool IsConfigured { get; }

    /// <summary>Human-readable transport name, for logs and diagnostics.</summary>
    string TransportName { get; }

    Task SendAsync(IReadOnlyList<string> to, string subject, string htmlBody, CancellationToken ct);
}
