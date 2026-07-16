using MeetUp.Api.Data;
using MeetUp.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace MeetUp.Api.Tests;

public sealed class CustomWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // Inject test-only configuration values. Secrets are not committed
        // (see Phase 0-B), so the test host provides its own JWT key.
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-jwt-signing-key-for-integration-tests-not-used-in-production-abcdefghij",
                ["Jwt:Issuer"] = "https://api.meetup.test",
                ["Jwt:Audience"] = "https://meetup.test",
                ["Jwt:ExpiresMinutes"] = "180",

                // LiveKit test values — dummy but valid. CreateRoomAsync will fail
                // (no LiveKit container in tests) but MeetingsController swallows that
                // exception, so tests still get 200 responses with valid tokens.
                ["LiveKit:ApiKey"] = "test-api-key",
                ["LiveKit:ApiSecret"] = "test-api-secret-must-be-32-chars-long-for-hmac-signing",
                ["LiveKit:WsUrl"] = "ws://localhost:7880",
                ["LiveKit:HttpUrl"] = "http://localhost:7880",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(AppDbContext));

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseNpgsql(_postgres.GetConnectionString());
            });

            var serviceProvider = services.BuildServiceProvider();
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.Migrate();
        });
    }

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16")
            .Build();

        await _postgres.StartAsync();
    }

    public new async Task DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }
}

