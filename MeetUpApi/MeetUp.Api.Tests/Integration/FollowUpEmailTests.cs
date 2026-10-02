using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Dtos.Meetings;
using MeetUp.Api.Entities;
using MeetUp.Api.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Phase 9: sending the AI-drafted follow-up email. Covers who receives it, that sending is a
/// one-way door, the opt-out, the audit trail, and that an untrusted draft cannot smuggle markup
/// into recipients' inboxes.
/// </summary>
public class FollowUpEmailTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public FollowUpEmailTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Email.Reset();
    }

    [Fact]
    public async Task Sends_To_Every_Participant_Marks_Sent_And_Records_An_Audit_Row()
    {
        var host = await CreateUser("fu-host");
        var attendee = await CreateUser("fu-attendee");

        var meetingId = await CreateMeeting(host.Token, "Q3 planning");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await AddParticipantAsync(meetingId, attendee.UserId, ParticipantRole.Participant);
        await SeedDraftAsync(meetingId, "Recap: Q3 planning", "We agreed to **ship on Friday**.");

        var response = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var sent = Assert.Single(_factory.Email.Sent);
        Assert.Equal("Recap: Q3 planning", sent.Subject);
        Assert.Contains(host.Email, sent.To);
        Assert.Contains(attendee.Email, sent.To);

        // Markdown became HTML, and the meeting title is in the wrapper.
        Assert.Contains("<strong>ship on Friday</strong>", sent.HtmlBody);
        Assert.Contains("Q3 planning", sent.HtmlBody);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var email = await db.FollowUpEmails.SingleAsync(e => e.MeetingId == meetingId);
        Assert.Equal(FollowUpEmailStatus.Sent, email.Status);
        Assert.NotNull(email.SentUtc);

        var audit = await db.FollowUpEmailRecipients
            .Where(r => r.FollowUpEmailId == email.Id)
            .ToListAsync();
        Assert.Equal(2, audit.Count);
        Assert.Contains(audit, r => r.RecipientEmail == attendee.Email);
    }

    [Fact]
    public async Task Sending_Twice_Is_Refused_And_Does_Not_Mail_Anyone_Again()
    {
        var host = await CreateUser("fu-twice");
        var meetingId = await CreateMeeting(host.Token, "Only once");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await SeedDraftAsync(meetingId, "First send", "Body.");

        var first = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        var sendsAfterFirst = _factory.Email.Sent.Count;

        var second = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // The refusal must be before the transport, not after.
        Assert.Equal(sendsAfterFirst, _factory.Email.Sent.Count);
    }

    [Fact]
    public async Task Opted_Out_Participants_Are_Excluded()
    {
        var host = await CreateUser("fu-opt-host");
        var optedOut = await CreateUser("fu-opt-out");

        var meetingId = await CreateMeeting(host.Token, "Opt out");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await AddParticipantAsync(meetingId, optedOut.UserId, ParticipantRole.Participant);
        await SetOptOutAsync(optedOut.UserId, true);
        await SeedDraftAsync(meetingId, "Excluding someone", "Body.");

        // The preview reports the exclusion before anything is sent.
        var preview = await SendAuthed(HttpMethod.Get, $"/api/meetings/{meetingId}/follow-up-email/recipients", host.Token);
        preview.EnsureSuccessStatusCode();
        var recipients = await preview.Content.ReadFromJsonAsync<FollowUpRecipientsDto>();
        Assert.Equal(1, recipients!.OptedOutCount);
        Assert.DoesNotContain(recipients.Recipients, r => r.Email == optedOut.Email);
        Assert.True(recipients.CanSend);

        var response = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var sent = Assert.Single(_factory.Email.Sent);
        Assert.DoesNotContain(optedOut.Email, sent.To);
        Assert.Contains(host.Email, sent.To);
    }

    [Fact]
    public async Task Raw_Html_In_The_Draft_Is_Escaped_Rather_Than_Passed_Through()
    {
        var host = await CreateUser("fu-xss");
        var meetingId = await CreateMeeting(host.Token, "Untrusted draft");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);

        // The draft starts as model output and is host-editable, so it is untrusted input.
        await SeedDraftAsync(
            meetingId,
            "Careful",
            "Recap <script>alert('xss')</script> and <img src=x onerror=alert(1)>.");

        var response = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var sent = Assert.Single(_factory.Email.Sent);

        // What matters is that no element is created: the delimiters are escaped, so the payload
        // renders as visible text. "onerror=" still appears as escaped text, which is inert.
        Assert.DoesNotContain("<script", sent.HtmlBody);
        Assert.DoesNotContain("<img", sent.HtmlBody);
        Assert.Contains("&lt;script&gt;", sent.HtmlBody);
        Assert.Contains("&lt;img", sent.HtmlBody);
    }

    [Fact]
    public async Task Only_The_Host_May_Send()
    {
        var host = await CreateUser("fu-owner");
        var attendee = await CreateUser("fu-guest");

        var meetingId = await CreateMeeting(host.Token, "Host only");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await AddParticipantAsync(meetingId, attendee.UserId, ParticipantRole.Participant);
        await SeedDraftAsync(meetingId, "Nope", "Body.");

        var response = await SendAuthed(
            HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", attendee.Token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Email.Sent);
    }

    [Fact]
    public async Task Sending_Without_A_Draft_Returns_404()
    {
        var host = await CreateUser("fu-nodraft");
        var meetingId = await CreateMeeting(host.Token, "No draft");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);

        var response = await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_Sent_Email_Can_No_Longer_Be_Edited()
    {
        var host = await CreateUser("fu-locked");
        var meetingId = await CreateMeeting(host.Token, "Locked after send");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await SeedDraftAsync(meetingId, "Locking", "Body.");

        await SendAuthed(HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);

        var edit = await SendAuthed(
            HttpMethod.Put, $"/api/meetings/{meetingId}/follow-up-email", host.Token,
            new UpdateFollowUpEmailRequestDto { Subject = "Changed", BodyMarkdown = "Changed." });

        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
    }

    [Fact]
    public async Task A_Failed_Send_Leaves_The_Draft_Sendable()
    {
        var host = await CreateUser("fu-failed");
        var meetingId = await CreateMeeting(host.Token, "Provider down");
        await AddParticipantAsync(meetingId, host.UserId, ParticipantRole.Host);
        await SeedDraftAsync(meetingId, "Will fail", "Body.");

        _factory.Email.FailWith = new InvalidOperationException("provider rejected the send");
        try
        {
            var response = await SendAuthed(
                HttpMethod.Post, $"/api/meetings/{meetingId}/follow-up-email/send", host.Token);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
        finally
        {
            _factory.Email.FailWith = null;
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var email = await db.FollowUpEmails.SingleAsync(e => e.MeetingId == meetingId);

        // Nothing was delivered, so the draft must not be marked Sent — otherwise the host could
        // never retry and nobody would ever receive it.
        Assert.Equal(FollowUpEmailStatus.Draft, email.Status);
        Assert.Null(email.SentUtc);
        Assert.Empty(await db.FollowUpEmailRecipients.Where(r => r.FollowUpEmailId == email.Id).ToListAsync());
    }

    [Fact]
    public async Task Opt_Out_Preference_Round_Trips()
    {
        var user = await CreateUser("fu-pref");

        var patch = await SendAuthed(HttpMethod.Patch, "/api/me/preferences", user.Token,
            new UserPreferencesDto { OptOutFollowUpEmails = true });
        Assert.Equal(HttpStatusCode.NoContent, patch.StatusCode);

        var read = await SendAuthed(HttpMethod.Get, "/api/me/preferences", user.Token);
        read.EnsureSuccessStatusCode();
        var prefs = await read.Content.ReadFromJsonAsync<UserPreferencesDto>();
        Assert.True(prefs!.OptOutFollowUpEmails);
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    private async Task SeedDraftAsync(Guid meetingId, string subject, string bodyMarkdown)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        db.FollowUpEmails.Add(new FollowUpEmail
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            Subject = subject,
            BodyMarkdown = bodyMarkdown,
            Status = FollowUpEmailStatus.Draft,
            CreatedUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task AddParticipantAsync(Guid meetingId, string userId, ParticipantRole role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Creating a meeting already records its host as a participant, and (MeetingId, UserId) is
        // unique — so adding the host again here failed every test that did, before it ran.
        if (await db.MeetingParticipants.AnyAsync(p => p.MeetingId == meetingId && p.UserId == userId))
        {
            return;
        }

        db.MeetingParticipants.Add(new MeetingParticipant
        {
            Id = Guid.NewGuid(),
            MeetingId = meetingId,
            UserId = userId,
            Role = role,
            JoinedUtc = DateTime.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task SetOptOutAsync(string userId, bool optOut)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.SingleAsync(u => u.Id == userId);
        user.OptOutFollowUpEmails = optOut;
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendAuthed(
        HttpMethod method, string url, string bearerToken, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return await _client.SendAsync(request);
    }

    private async Task<Guid> CreateMeeting(string bearerToken, string title)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("meetingId").GetGuid();
    }

    private async Task<(string UserId, string Email, string Token)> CreateUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}-{suffix}";
        var email = $"{username}@meetup.test";

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = email,
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();
        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userId = await db.Users.Where(u => u.UserName == username).Select(u => u.Id).SingleAsync();

        return (userId, email, auth!.Token);
    }
}
