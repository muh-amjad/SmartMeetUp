using MeetUp.Api.Dtos.Auth;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MeetUp.Api.Tests.Integration;

public class AuthFlowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthFlowTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Signup_Login_Refresh_And_Search_Should_Work()
    {
        var alphaSignup = new SignupRequestDto
        {
            Username = "alpha-user",
            Email = "alpha@meetup.test",
            Password = "Password123!"
        };

        var betaSignup = new SignupRequestDto
        {
            Username = "beta-user",
            Email = "beta@meetup.test",
            Password = "Password123!"
        };

        var alphaSignupResponse = await _client.PostAsJsonAsync("/api/auth/signup", alphaSignup);
        alphaSignupResponse.EnsureSuccessStatusCode();
        var alphaAuth = await alphaSignupResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(alphaAuth);
        Assert.False(string.IsNullOrWhiteSpace(alphaAuth!.Token));
        Assert.False(string.IsNullOrWhiteSpace(alphaAuth.RefreshToken));

        var betaSignupResponse = await _client.PostAsJsonAsync("/api/auth/signup", betaSignup);
        betaSignupResponse.EnsureSuccessStatusCode();

        var loginResponse = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequestDto
        {
            UsernameOrEmail = "alpha-user",
            Password = "Password123!"
        });
        loginResponse.EnsureSuccessStatusCode();
        var loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(loginAuth);
        Assert.False(string.IsNullOrWhiteSpace(loginAuth!.RefreshToken));

        var refreshResponse = await _client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequestDto
        {
            RefreshToken = loginAuth.RefreshToken
        });
        refreshResponse.EnsureSuccessStatusCode();
        var refreshedAuth = await refreshResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(refreshedAuth);
        Assert.NotEqual(loginAuth.Token, refreshedAuth!.Token);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/users/search?query=beta");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", refreshedAuth.Token);
        var searchResponse = await _client.SendAsync(request);
        searchResponse.EnsureSuccessStatusCode();
        var json = await searchResponse.Content.ReadAsStringAsync();

        using var document = JsonDocument.Parse(json);
        var results = document.RootElement;
        Assert.True(results.GetArrayLength() >= 1);
        Assert.Contains(results.EnumerateArray(), element =>
            string.Equals(element.GetProperty("username").GetString(), "beta-user", StringComparison.Ordinal));
    }
}
