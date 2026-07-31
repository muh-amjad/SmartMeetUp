using MeetUp.Api.Dtos.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

/// <summary>
/// Phase 10 production hardening: the response headers a browser relies on, and the rate limits that
/// protect the free-tier quotas.
/// </summary>
public class SecurityHeaderTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public SecurityHeaderTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Security_Headers_Are_Present_On_A_Normal_Response()
    {
        var response = await _client.GetAsync("/health/live");

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal(
            "strict-origin-when-cross-origin",
            Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("camera=()", Assert.Single(response.Headers.GetValues("Permissions-Policy")));
    }

    [Fact]
    public async Task Security_Headers_Are_Also_Present_On_An_Error_Response()
    {
        // Headers must not depend on the happy path — a 401 is still a response a browser renders.
        var response = await _client.GetAsync("/api/meetings");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Hsts_Is_Absent_When_Disabled()
    {
        // Sending HSTS over plain HTTP in dev would pin the browser to https://localhost.
        var response = await _client.GetAsync("/health/live");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}

/// <summary>
/// Host with only the auth ceiling lowered. Kept separate from the meeting-cap host because a low
/// auth limit would otherwise throttle the tests' own signups.
/// </summary>
public sealed class ThrottledAuthFactory : CustomWebApplicationFactory
{
    public const int AuthLimit = 3;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        // Layered after the base configuration, so this wins.
        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RateLimits:AuthPerMinute"] = AuthLimit.ToString(),
            }));
    }
}

/// <summary>Host with only the daily meeting cap lowered; signups stay unthrottled.</summary>
public sealed class ThrottledMeetingsFactory : CustomWebApplicationFactory
{
    public const int MeetingLimit = 2;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:RateLimits:MeetingsPerDay"] = MeetingLimit.ToString(),
            }));
    }
}

public class AuthRateLimitTests : IClassFixture<ThrottledAuthFactory>
{
    private readonly HttpClient _client;

    public AuthRateLimitTests(ThrottledAuthFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Auth_Endpoints_Are_Throttled_Per_Client_With_A_429_And_Retry_After()
    {
        HttpResponseMessage? throttled = null;

        // Deliberately uses login with accounts that do not exist, so the test needs no signup and
        // cannot be tripped up by its own setup traffic.
        for (var i = 0; i < ThrottledAuthFactory.AuthLimit + 2; i++)
        {
            var response = await _client.PostAsJsonAsync("/api/auth/login", new
            {
                usernameOrEmail = $"nobody-{i}@meetup.test",
                password = "WrongPassword123!",
            });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throttled = response;
                break;
            }
        }

        Assert.NotNull(throttled);
        Assert.True(
            throttled!.Headers.Contains("Retry-After"),
            "a throttled response should say when to try again");
    }
}

public class MeetingCapTests : IClassFixture<ThrottledMeetingsFactory>
{
    private readonly HttpClient _client;

    public MeetingCapTests(ThrottledMeetingsFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Meeting_Creation_Is_Capped_Per_User()
    {
        var user = await CreateUser("ratelimit-meetings");

        // Up to the cap must succeed.
        for (var i = 0; i < ThrottledMeetingsFactory.MeetingLimit; i++)
        {
            var allowed = await CreateMeeting(user.Token);
            Assert.True(
                allowed.IsSuccessStatusCode,
                $"meeting {i + 1} of {ThrottledMeetingsFactory.MeetingLimit} should be allowed, got {(int)allowed.StatusCode}");
        }

        // The next one is over the daily budget.
        var blocked = await CreateMeeting(user.Token);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }

    [Fact]
    public async Task One_Users_Cap_Does_Not_Affect_Another_User()
    {
        var heavy = await CreateUser("ratelimit-heavy");
        var quiet = await CreateUser("ratelimit-quiet");

        // Exhaust the first user's daily budget.
        for (var i = 0; i < ThrottledMeetingsFactory.MeetingLimit; i++)
        {
            await CreateMeeting(heavy.Token);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await CreateMeeting(heavy.Token)).StatusCode);

        // The limit partitions per user, so the second account still has its own budget.
        var other = await CreateMeeting(quiet.Token);
        Assert.True(
            other.IsSuccessStatusCode,
            $"a different user should be unaffected, got {(int)other.StatusCode}");
    }

    // ─────────────────────────────  helpers  ─────────────────────────────

    private Task<HttpResponseMessage> CreateMeeting(string bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/meetings")
        {
            Content = JsonContent.Create(new { title = "Rate limit probe" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return _client.SendAsync(request);
    }

    private async Task<(string Username, string Token)> CreateUser(string prefix)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}-{suffix}";

        var response = await _client.PostAsJsonAsync("/api/auth/signup", new SignupRequestDto
        {
            Username = username,
            Email = $"{username}@meetup.test",
            Password = "Password123!",
        });
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (username, doc.RootElement.GetProperty("token").GetString()!);
    }
}
