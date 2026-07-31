namespace MeetUp.Api.Services.Email;

/// <summary>
/// Registered when neither Resend nor SMTP is set up. It exists so that the absence of an email
/// transport is a clear, reportable state rather than a missing dependency that fails DI at startup
/// — every other feature keeps working, and only sending refuses.
/// </summary>
public sealed class UnconfiguredEmailService : IEmailService
{
    public bool IsConfigured => false;

    public string TransportName => "none";

    public Task SendAsync(IReadOnlyList<string> to, string subject, string htmlBody, CancellationToken ct) =>
        throw new InvalidOperationException("No email transport is configured.");
}
