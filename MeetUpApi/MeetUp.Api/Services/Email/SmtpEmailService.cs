using System.Net.Mail;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services.Email;

/// <summary>
/// Plain SMTP with no auth, aimed at a local mail catcher (Mailpit) during development. It lets the
/// whole send path be exercised — recipient selection, markdown rendering, audit rows — without a
/// provider account or a verified sending domain.
///
/// Uses the framework's SmtpClient rather than MailKit: this transport is only ever pointed at a
/// local catcher, so MailKit's TLS and auth handling would buy nothing here.
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly EmailOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<EmailOptions> options, ILogger<SmtpEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Smtp.Host) &&
        !string.IsNullOrWhiteSpace(_options.FromEmail);

    public string TransportName => $"SMTP ({_options.Smtp.Host}:{_options.Smtp.Port})";

    public async Task SendAsync(
        IReadOnlyList<string> to, string subject, string htmlBody, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("SMTP is not configured.");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(_options.FromEmail, _options.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };

        foreach (var recipient in to)
        {
            message.To.Add(recipient);
        }

        using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port);
        await client.SendMailAsync(message, ct);

        _logger.LogInformation(
            "Sent follow-up email to {RecipientCount} recipient(s) via {Transport}", to.Count, TransportName);
    }
}
