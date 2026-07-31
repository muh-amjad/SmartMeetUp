using System.Net;
using Markdig;

namespace MeetUp.Api.Services.Email;

/// <summary>
/// Turns the stored markdown draft into the HTML body that gets emailed.
/// </summary>
public static class FollowUpEmailRenderer
{
    /// <summary>
    /// DisableHtml is the important part: raw HTML in the source is escaped rather than passed
    /// through. The draft starts as LLM output and is then editable by the host, so it is untrusted
    /// input that ends up in other people's inboxes — without this, a crafted draft could smuggle
    /// arbitrary markup into every recipient's mail client.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UseAutoLinks()
        .Build();

    public static string Render(string subject, string bodyMarkdown, string meetingTitle)
    {
        var body = Markdown.ToHtml(bodyMarkdown ?? string.Empty, Pipeline);

        // Inline styles only — email clients routinely drop <style> blocks and external CSS.
        return $"""
            <!doctype html>
            <html>
              <body style="margin:0;padding:24px;background:#f4f5f7;font-family:-apple-system,Segoe UI,Roboto,sans-serif;">
                <div style="max-width:600px;margin:0 auto;background:#ffffff;border:1px solid #e3e6ea;border-radius:8px;padding:28px 32px;">
                  <p style="margin:0 0 4px;font-size:12px;letter-spacing:0.08em;text-transform:uppercase;color:#6b7280;">
                    Meeting follow-up
                  </p>
                  <h1 style="margin:0 0 20px;font-size:20px;line-height:1.3;color:#111827;">
                    {WebUtility.HtmlEncode(meetingTitle)}
                  </h1>
                  <div style="font-size:15px;line-height:1.6;color:#1f2937;">
                    {body}
                  </div>
                  <p style="margin:28px 0 0;padding-top:16px;border-top:1px solid #e3e6ea;font-size:12px;color:#6b7280;">
                    Sent from SmartMeetUp. You are receiving this because you attended this meeting —
                    you can turn these off in your settings.
                  </p>
                </div>
              </body>
            </html>
            """;
    }
}
