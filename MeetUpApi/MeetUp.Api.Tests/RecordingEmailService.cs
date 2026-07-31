using MeetUp.Api.Services.Email;

namespace MeetUp.Api.Tests;

public sealed record SentEmail(IReadOnlyList<string> To, string Subject, string HtmlBody);

/// <summary>
/// Test transport that records sends instead of delivering them, so the whole send path — recipient
/// selection, rendering, audit rows, status transitions — can be asserted without a mail server.
/// </summary>
public sealed class RecordingEmailService : IEmailService
{
    private readonly List<SentEmail> _sent = new();

    public bool IsConfigured { get; set; } = true;

    public string TransportName => "recording (test)";

    public IReadOnlyList<SentEmail> Sent => _sent;

    /// <summary>Set to simulate a provider rejecting the send.</summary>
    public Exception? FailWith { get; set; }

    public Task SendAsync(IReadOnlyList<string> to, string subject, string htmlBody, CancellationToken ct)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        _sent.Add(new SentEmail(to.ToList(), subject, htmlBody));
        return Task.CompletedTask;
    }

    public void Reset()
    {
        _sent.Clear();
        FailWith = null;
        IsConfigured = true;
    }
}
