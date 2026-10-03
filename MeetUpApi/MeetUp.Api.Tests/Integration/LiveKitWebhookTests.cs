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

    [Fact]
    public async Task Accepts_Events_That_Carry_Fields_Newer_Than_The_Sdk_Knows()
    {
        var (meetingId, roomName) = await CreateMeetingAsync("webhook-new-fields");

        // LiveKit Cloud sends roomEndReason on room_finished. The SDK's strict parser rejected it,
        // so the event was dropped and the meeting never got an end time.
        var body = RoomFinished(roomName, extraField: "\"roomEndReason\":\"ROOM_CLOSED\"");

        var response = await PostWebhookAsync(body, Sign(body, ApiSecret));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meeting = await db.Meetings.AsNoTracking().SingleAsync(m => m.Id == meetingId);

        Assert.NotNull(meeting.EndedUtc);
        Assert.Equal(MeetingStatus.Processing, meeting.Status);
    }

    [Fact]
    public async Task Rejects_A_Body_Changed_After_It_Was_Signed()
    {
        var (_, roomName) = await CreateMeetingAsync("webhook-tampered");
        var signedBody = RoomFinished(roomName);
        var tamperedBody = signedBody.Replace(roomName, "meeting-someone-else");

        var response = await PostWebhookAsync(tamperedBody, Sign(signedBody, ApiSecret));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Rejects_A_Token_Signed_With_Another_Secret()
    {
        var (meetingId, roomName) = await CreateMeetingAsync("webhook-forged");
        var body = RoomFinished(roomName);

        var response = await PostWebhookAsync(body, Sign(body, "a-forged-secret-that-is-also-long-enough-to-sign"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var meeting = await db.Meetings.AsNoTracking().SingleAsync(m => m.Id == meetingId);
        Assert.Null(meeting.EndedUtc);
    }

    [Fact]
    public async Task Rejects_A_Request_With_No_Signature()
    {
        var (_, roomName) = await CreateMeetingAsync("webhook-unsigned");

        var response = await PostWebhookAsync(RoomFinished(roomName), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ────────────── helpers ──────────────

    private static string RoomFinished(string roomName, string? extraField = null)
    {
        var extra = extraField is null ? string.Empty : "," + extraField;
        return $$"""{"event":"room_finished","room":{"sid":"RM_test","name":"{{roomName}}"},"id":"EV_test","createdAt":"1790000000"{{extra}}}""";
    }

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

    private async Task<(Guid MeetingId, string RoomName)> CreateMeetingAsync(string prefix)
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
        return (
            Guid.Parse(doc.RootElement.GetProperty("meetingId").GetString()!),
            doc.RootElement.GetProperty("roomName").GetString()!);
    }
}
