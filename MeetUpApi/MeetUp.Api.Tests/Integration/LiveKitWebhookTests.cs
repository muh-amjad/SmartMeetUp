using MeetUp.Api.Data;
using MeetUp.Api.Dtos.Auth;
using MeetUp.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Webhooks are signed the way LiveKit signs them: an HS256 JWT whose issuer is the API key and
/// whose "sha256" claim is the SHA-256 of the exact body.
/// </summary>
public class LiveKitWebhookTests : IClassFixture<CustomWebApplicationFactory>
{
    // Must match the LiveKit values CustomWebApplicationFactory configures.
    private const string ApiKey = "test-api-key";
    private const string ApiSecret = "test-api-secret-must-be-32-chars-long-for-hmac-signing";

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public LiveKitWebhookTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ─────────────────────────────  signature  ─────────────────────────────

    [Fact]
    public async Task Accepts_Events_That_Carry_Fields_Newer_Than_The_Sdk_Knows()
    {
        var meeting = await CreateMeetingAsync("webhook-new-fields");

        // LiveKit Cloud sends roomEndReason on room_finished. The SDK's strict parser rejected it,
        // so the event was dropped and the meeting never got an end time.
        var body = RoomFinished(meeting.RoomName, extraField: "\"roomEndReason\":\"ROOM_CLOSED\"");

        var response = await PostWebhookAsync(body, Sign(body, ApiSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull((await LoadAsync(meeting.Id)).EndedUtc);
    }

    [Fact]
    public async Task Rejects_A_Body_Changed_After_It_Was_Signed()
    {
        var meeting = await CreateMeetingAsync("webhook-tampered");
        var signedBody = RoomFinished(meeting.RoomName);
        var tamperedBody = signedBody.Replace(meeting.RoomName, "meeting-someone-else");

        var response = await PostWebhookAsync(tamperedBody, Sign(signedBody, ApiSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_A_Token_Signed_With_Another_Secret()
    {
        var meeting = await CreateMeetingAsync("webhook-forged");
        var body = RoomFinished(meeting.RoomName);

        var response = await PostWebhookAsync(body, Sign(body, "a-forged-secret-that-is-also-long-enough-to-sign"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null((await LoadAsync(meeting.Id)).EndedUtc);
    }

    [Fact]
    public async Task Rejects_A_Request_With_No_Signature()
    {
        var meeting = await CreateMeetingAsync("webhook-unsigned");

        var response = await PostWebhookAsync(RoomFinished(meeting.RoomName), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ─────────────────────────────  meeting lifecycle  ─────────────────────────────

    [Fact]
    public async Task A_Recorded_Meeting_Is_Processing_Once_The_Room_Closes()
    {
        var meeting = await CreateMeetingAsync("lifecycle-recorded");
        await UpdateAsync(meeting.Id, m => { m.Status = MeetingStatus.Live; m.EgressId = "EG_recording"; });

        await PostSignedAsync(RoomFinished(meeting.RoomName));

        var stored = await LoadAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Processing, stored.Status);
        Assert.NotNull(stored.EndedUtc);
    }

    [Fact]
    public async Task A_Meeting_With_No_Recording_Ends_Instead_Of_Waiting_In_Processing()
    {
        // The meeting list will not open a Processing meeting. One with no recording coming used to
        // be marked Processing anyway, and was then locked out of the list for good.
        var meeting = await CreateMeetingAsync("lifecycle-unrecorded");
        await UpdateAsync(meeting.Id, m => { m.Status = MeetingStatus.Live; m.EgressId = null; });

        await PostSignedAsync(RoomFinished(meeting.RoomName));

        var stored = await LoadAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Ended, stored.Status);
        Assert.NotNull(stored.EndedUtc);
    }

    [Fact]
    public async Task A_Recording_That_Fails_Mid_Call_Leaves_The_Meeting_Open()
    {
        var meeting = await CreateMeetingAsync("lifecycle-recorder-died");
        await UpdateAsync(meeting.Id, m => { m.Status = MeetingStatus.Live; m.EgressId = "EG_dies_early"; });

        // The recorder gives up while people are still talking.
        await PostSignedAsync(EgressEnded("EG_dies_early", meeting.RoomName, "EGRESS_ABORTED", "Start signal not received"));

        var duringCall = await LoadAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Live, duringCall.Status);
        Assert.Null(duringCall.EndedUtc);

        // Someone can still join.
        var join = await SendAuthedAsync(HttpMethod.Post, $"/api/meetings/{meeting.Id}/join", meeting.Token);
        Assert.Equal(HttpStatusCode.OK, join.StatusCode);

        // When the call ends it closes as unrecorded, rather than waiting for a recording forever.
        await PostSignedAsync(RoomFinished(meeting.RoomName));

        var afterCall = await LoadAsync(meeting.Id);
        Assert.Equal(MeetingStatus.Ended, afterCall.Status);
        Assert.NotNull(afterCall.EndedUtc);
    }

    [Fact]
    public async Task A_Finished_Meeting_Cannot_Be_Rejoined_Whatever_Its_Status()
    {
        var meeting = await CreateMeetingAsync("lifecycle-rejoin");
        await UpdateAsync(meeting.Id, m => { m.Status = MeetingStatus.Live; m.EgressId = "EG_recording"; });

        await PostSignedAsync(RoomFinished(meeting.RoomName));
        Assert.Equal(MeetingStatus.Processing, (await LoadAsync(meeting.Id)).Status);

        // Only Ended used to be refused; a Processing, Ready or Failed meeting could be reopened.
        var join = await SendAuthedAsync(HttpMethod.Post, $"/api/meetings/{meeting.Id}/join", meeting.Token);
        Assert.Equal(HttpStatusCode.Conflict, join.StatusCode);
    }

    // ────────────── helpers ──────────────

    private sealed record TestMeeting(Guid Id, string RoomName, string Token);

    private static string RoomFinished(string roomName, string? extraField = null)
    {
        var extra = extraField is null ? string.Empty : "," + extraField;
        return $$"""{"event":"room_finished","room":{"sid":"RM_test","name":"{{roomName}}"},"id":"EV_test","createdAt":"1790000000"{{extra}}}""";
    }

    private static string EgressEnded(string egressId, string roomName, string status, string error) =>
        $$"""{"event":"egress_ended","egressInfo":{"egressId":"{{egressId}}","roomName":"{{roomName}}","status":"{{status}}","error":"{{error}}"},"id":"EV_egress","createdAt":"1790000000"}""";

    private static string Sign(string body, string secret)
    {
        var checksum = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: ApiKey,
            claims: new[] { new Claim("sha256", checksum) },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task PostSignedAsync(string body)
    {
        var response = await PostWebhookAsync(body, Sign(body, ApiSecret));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(string body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/livekit")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/webhook+json"),
        };

        // LiveKit sends the bare token, without a "Bearer" scheme.
        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", token);
        }

        return await _client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAuthedAsync(HttpMethod method, string url, string bearerToken)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return await _client.SendAsync(request);
    }

    private async Task<Meeting> LoadAsync(Guid meetingId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Meetings.AsNoTracking().SingleAsync(m => m.Id == meetingId);
    }

    private async Task UpdateAsync(Guid meetingId, Action<Meeting> change)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meeting = await db.Meetings.SingleAsync(m => m.Id == meetingId);
        change(meeting);
        await db.SaveChangesAsync();
    }

    private async Task<TestMeeting> CreateMeetingAsync(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var signup = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = $"{prefix}-{suffix}",
            Email = $"{prefix}-{suffix}@meetup.test",
            Password = "Password123!",
        });
        signup.EnsureSuccessStatusCode();
        var auth = await signup.Content.ReadFromJsonAsync<AuthResponseDto>();

        var create = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title = "Webhook test" }),
        };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var response = await _client.SendAsync(create);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return new TestMeeting(
            Guid.Parse(doc.RootElement.GetProperty("meetingId").GetString()!),
            doc.RootElement.GetProperty("roomName").GetString()!,
            auth.Token);
    }
}
