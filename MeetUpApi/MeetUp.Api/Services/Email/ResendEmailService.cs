using System.Net.Http.Headers;
using System.Net.Http.Json;
using MeetUp.Api.Options;
using Microsoft.Extensions.Options;

namespace MeetUp.Api.Services.Email;

/// <summary>
/// Resend via its REST API. Implemented against HTTP rather than a community SDK for the same
/// reason as the other providers in this codebase: the surface needed here is a single POST, and a
/// wrapper package would add a dependency whose object shapes have to be reverse-engineered.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private const string SendUrl = "https://api.resend.com/emails";

    private readonly HttpClient _http;
    private readonly EmailOptions _options;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        HttpClient http,
        IOptions<EmailOptions> options,
        ILogger<ResendEmailService> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.Resend.ApiKey) &&
        !string.IsNullOrWhiteSpace(_options.FromEmail);

    public string TransportName => "Resend";

    public async Task SendAsync(
        IReadOnlyList<string> to, string subject, string htmlBody, CancellationToken ct)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Resend is not configured.");
        }

        var request = new HttpRequestMessage(HttpMethod.Post, SendUrl)
        {
            Content = JsonContent.Create(new
            {
                from = $"{_options.FromName} <{_options.FromEmail}>",
                to,
                subject,
                html = htmlBody,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Resend.ApiKey);

        var response = await _http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Resend explains rejections (unverified domain, invalid address) in the body, so keep it.
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Resend rejected the send ({(int)response.StatusCode}): {body}");
        }

        _logger.LogInformation("Sent follow-up email to {RecipientCount} recipient(s) via Resend", to.Count);
    }
}
