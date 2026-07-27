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

                // Blob storage test values — dummy but non-empty so the AWS S3 client can be
                // constructed. No MinIO runs in tests; the egress/recording calls that would
                // hit it are swallowed by the controller, so tests never actually upload.
                ["BlobStorage:ServiceUrl"] = "http://localhost:9000",
                ["BlobStorage:EgressServiceUrl"] = "http://minio:9000",
                ["BlobStorage:AccessKey"] = "test-access-key",
                ["BlobStorage:SecretKey"] = "test-secret-key",
                ["BlobStorage:BucketName"] = "test-recordings",
                ["BlobStorage:Region"] = "us-east-1",
                ["BlobStorage:ForcePathStyle"] = "true",

                ["AssemblyAi:ApiKey"] = "test-assemblyai-key",
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<AppDbContext>));
            services.RemoveAll(typeof(AppDbContext));

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseNpgsql(_postgres.GetConnectionString(), npgsql => npgsql.UseVector());
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

