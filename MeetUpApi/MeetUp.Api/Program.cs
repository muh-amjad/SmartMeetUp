
using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using MeetUp.Api.Jobs;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using MeetUp.Api.Services.Ai;
using MeetUp.Api.Services.AssemblyAi;
using MeetUp.Api.Services.Email;
using MeetUp.Api.Services.Search;
using MeetUp.Api.Infrastructure;
using MeetUp.Api.Infrastructure.Middleware;
using MeetUp.Api.Infrastructure.Logging;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.Npgsql;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using Serilog.Core;
using System.Text;

namespace MeetUp.Api
{
    public partial class Program
    {
        public static async Task Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // IHttpContextAccessor is needed by the Serilog UserIdEnricher below.
            builder.Services.AddHttpContextAccessor();
            builder.Services.AddSingleton<ILogEventEnricher, UserIdEnricher>();

            // Configure Serilog. The UserIdEnricher pulls the authenticated user id from
            // the current HttpContext so every log entry made while handling a request is
            // tagged with a UserId property when available.
            builder.Host.UseSerilog((context, services, loggerConfig) =>
            {
                loggerConfig
                    .ReadFrom.Configuration(context.Configuration)
                    .ReadFrom.Services(services)
                    .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName)
                    // ASP.NET's exception middleware logs every exception at Error before
                    // GlobalExceptionHandler decides what it means, so each routine 404 used to
                    // appear as an error with a stack trace. Drop only that entry, and only for the
                    // API's own client-error exceptions; GlobalExceptionHandler logs those itself at
                    // Information. Genuine failures still come through at Error.
                    .Filter.ByExcluding(logEvent =>
                        logEvent.Exception is not null
                        && GlobalExceptionHandler.IsClientError(logEvent.Exception)
                        && logEvent.Properties.TryGetValue("SourceContext", out var source)
                        && source.ToString().Contains("ExceptionHandlerMiddleware", StringComparison.Ordinal));
            });

            builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
            builder.Services.Configure<MeetingOptions>(builder.Configuration.GetSection(MeetingOptions.Section));
            builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection(LiveKitOptions.SectionName));
            builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection(BlobStorageOptions.SectionName));
            builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName));
            builder.Services.Configure<AssemblyAiOptions>(builder.Configuration.GetSection(AssemblyAiOptions.SectionName));
            builder.Services.Configure<AiProvidersOptions>(builder.Configuration.GetSection(AiProvidersOptions.SectionName));
            builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));

            // EF Core gets a data source of its own with pgvector registered on it directly.
            //
            // Registering it only through the EF options (npgsql.UseVector()) adds the vector type to
            // Npgsql's *global* list, which Npgsql reads once, when it first builds the connection pool
            // for a connection string. Hangfire opens a connection with this same string during
            // startup, before any DbContext exists, so that pool was built without the vector type —
            // and every embedding write failed with "Writing values of 'Pgvector.Vector' is not
            // supported". It never showed up locally only because embeddings are skipped without a
            // Gemini key. A dedicated data source does not depend on what connected first.
            builder.Services.AddSingleton(_ =>
            {
                var dataSourceBuilder = new NpgsqlDataSourceBuilder(
                    builder.Configuration.GetConnectionString("DefaultConnection"));
                dataSourceBuilder.UseVector();
                return dataSourceBuilder.Build();
            });

            // UseVector here is still needed: this one maps the Vector type for EF Core's queries
            // (column type, CosineDistance), the one above for the database driver.
            builder.Services.AddDbContext<AppDbContext>((services, options) =>
                options.UseNpgsql(
                    services.GetRequiredService<NpgsqlDataSource>(),
                    npgsql => npgsql.UseVector()));

            builder.Services
                .AddIdentityCore<ApplicationUser>(options =>
                {
                    options.User.RequireUniqueEmail = true;
                    options.Password.RequireDigit = true;
                    options.Password.RequireLowercase = true;
                    options.Password.RequireUppercase = true;
                    options.Password.RequiredLength = 8;
                })
                .AddRoles<IdentityRole>()
                .AddEntityFrameworkStores<AppDbContext>()
                .AddSignInManager();

            builder.Services.AddScoped<IUserRepository, UserRepository>();
            builder.Services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            builder.Services.AddScoped<IMeetingRepository, MeetingRepository>();
            builder.Services.AddScoped<IMeetingParticipantRepository, MeetingParticipantRepository>();   // ← naya
            builder.Services.AddScoped<IChatMessageRepository, ChatMessageRepository>();                 // ← naya
            builder.Services.AddScoped<ITranscriptRepository, TranscriptRepository>();
            builder.Services.AddScoped<IMeetingAnalysisRepository, MeetingAnalysisRepository>();
            builder.Services.AddScoped<IMeetingAnalyticsRepository, MeetingAnalyticsRepository>();
            builder.Services.AddScoped<ISearchRepository, SearchRepository>();

            builder.Services.AddScoped<ITokenService, TokenService>();
            builder.Services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
            builder.Services.AddSingleton<ILiveKitService, LiveKitService>();
            builder.Services.AddScoped<IBlobStorageService, S3BlobStorageService>();
            builder.Services.AddScoped<ITranscriptionJob, TranscriptionJob>();
            builder.Services.AddScoped<IAiAnalysisJob, AiAnalysisJob>();
            builder.Services.AddScoped<ISpeakerMappingJob, SpeakerMappingJob>();
            builder.Services.AddScoped<IEmbeddingJob, EmbeddingJob>();
            builder.Services.AddScoped<IStaleMeetingSweepJob, StaleMeetingSweepJob>();

            // Pick the email transport from what is actually configured: a real provider if there is
            // an API key, a local catcher if there is an SMTP host, otherwise a stub that reports
            // itself unconfigured so the send endpoint can refuse with a clear message.
            var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()
                ?? new EmailOptions();

            if (!string.IsNullOrWhiteSpace(emailOptions.Resend.ApiKey))
            {
                builder.Services.AddHttpClient<IEmailService, ResendEmailService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });
            }
            else if (!string.IsNullOrWhiteSpace(emailOptions.Smtp.Host))
            {
                builder.Services.AddScoped<IEmailService, SmtpEmailService>();
            }
            else
            {
                builder.Services.AddSingleton<IEmailService, UnconfiguredEmailService>();
            }
            builder.Services.AddScoped<ISearchService, HybridSearchService>();
            builder.Services.AddHttpClient<IEmbeddingService, GeminiEmbeddingService>(client =>
            {
                client.Timeout = TimeSpan.FromMinutes(2);
            });

            // The registry builds one provider per configured API key, so it needs a plain named
            // client rather than a typed one (base address and auth differ per provider).
            builder.Services.AddHttpClient("ai-analysis", client =>
            {
                // LLM calls on a long transcript routinely outlast the 100s default.
                client.Timeout = TimeSpan.FromMinutes(5);
            });
            builder.Services.AddSingleton<AnalysisProviderRegistry>();
            builder.Services.AddScoped<IAnalysisProviderFactory, AnalysisProviderFactory>();

            builder.Services.AddHttpClient<IAssemblyAiClient, AssemblyAiClient>((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<AssemblyAiOptions>>().Value;
                client.BaseAddress = new Uri(options.BaseUrl);
                // Recordings are video now, and with UploadRecordings on the whole file goes through
                // this client. The 100s default cuts off any upload past a few minutes of meeting.
                client.Timeout = TimeSpan.FromMinutes(30);
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    client.DefaultRequestHeaders.Add("Authorization", options.ApiKey);
                }
            });

            // Register FluentValidation validators
            builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);

            // Skipped under the integration-test host: CustomWebApplicationFactory swaps
            // AppDbContext to an ephemeral Testcontainers instance via ConfigureServices,
            // which happens too late for Hangfire to pick up the same connection string here
            // (it reads straight from configuration). None of the current tests exercise
            // background jobs, so there's nothing lost by not standing up a server for them.
            if (!builder.Environment.IsEnvironment("Testing"))
            {
                var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
                    ?? throw new InvalidOperationException("DefaultConnection is missing.");

                builder.Services.AddHangfire(config => config
                    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                    .UseSimpleAssemblyNameTypeSerializer()
                    .UseRecommendedSerializerSettings()
                    .UsePostgreSqlStorage(c => c.UseNpgsqlConnection(connectionString)));
                builder.Services.AddHangfireServer();
            }

            // Register global exception handler
            builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
            builder.Services.AddProblemDetails();

            // Health checks: /health/live is liveness only; /health/ready pings the database
            // through the EF Core check so we return 503 when the database is unreachable.
            builder.Services.AddHealthChecks()
                .AddDbContextCheck<AppDbContext>("database");

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
                        ?? throw new InvalidOperationException("Jwt configuration is missing.");

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateIssuerSigningKey = true,
                        ValidateLifetime = true,
                        ValidIssuer = jwtOptions.Issuer,
                        ValidAudience = jwtOptions.Audience,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key))
                    };

                    options.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            var path = context.HttpContext.Request.Path;

                            if (!string.IsNullOrWhiteSpace(accessToken)
                                && path.StartsWithSegments("/meetingHub", StringComparison.OrdinalIgnoreCase))
                            {
                                context.Token = accessToken;
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            builder.Services.AddAuthorization();
            builder.Services.AddControllers();
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(options =>
            {
                options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
                {
                    Title = "SmartMeetUp API",
                    Version = "v1",
                });

                // JWT Bearer scheme — Swagger UI ke top pe "Authorize" button add karega
                options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Name = "Authorization",
                    Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    In = Microsoft.OpenApi.Models.ParameterLocation.Header,
                    Description = "Paste ONLY the JWT token here (no 'Bearer ' prefix — Swagger adds it).",
                });

                // Har request pe ye scheme apply karo
                options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
                {
                    {
                        new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                        {
                            Reference = new Microsoft.OpenApi.Models.OpenApiReference
                            {
                                Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                                Id = "Bearer",
                            },
                        },
                        Array.Empty<string>()
                    }
                });
            });

            builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

            builder.Services.AddCors();

            // Configured through the options system rather than inside AddCors so the allow-list is
            // read once every configuration source is in place. Reading it during service
            // registration would miss anything layered in later and silently fall back to defaults.
            builder.Services.AddOptions<CorsOptions>()
                .Configure<IOptionsMonitor<SecurityOptions>>((cors, security) =>
                {
                    cors.AddPolicy("AllowAngular", policy => policy
                        // Credentials are allowed, so a wildcard origin is not an option here.
                        .WithOrigins(security.CurrentValue.AllowedOrigins)
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials());
                });

            builder.Services.AddRateLimiter(RateLimitPolicies.AddPolicies);

            // Behind Caddy the app sees the proxy's address and scheme unless it is told to read the
            // forwarded headers — without this, rate-limit partitions collapse onto one IP and any
            // generated URL comes out as http.
            builder.Services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

                // The proxy is a sibling container on an internal network, not a known static IP.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            builder.Services.AddSignalR();

            var app = builder.Build();

            // Must run before anything that reads the client address or scheme.
            app.UseForwardedHeaders();

            app.UseExceptionHandler();

            // Early enough that even error and rate-limited responses carry the headers.
            app.UseMiddleware<SecurityHeadersMiddleware>();

            app.UseCors("AllowAngular");

            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            // HTTPS redirect only in non-dev. In dev, HTTP webhooks from LiveKit
            // (docker container) can't follow redirects to our self-signed HTTPS cert.
            // Production Caddy already handles TLS in front of the API.
            if (!app.Environment.IsDevelopment())
            {
                app.UseHttpsRedirection();
            }

            app.UseAuthentication();
            app.UseAuthorization();

            // After authentication, so per-user partitions can actually see the user. Always in the
            // pipeline — the policies themselves become no-ops when limiting is switched off.
            app.UseRateLimiter();

            app.MapControllers();
            app.MapHub<Hubs.MeetingHub>("/meetingHub");
            app.MapHealthChecks("/health/live");
            app.MapHealthChecks("/health/ready");

            if (!app.Environment.IsEnvironment("Testing"))
            {
                app.MapHangfireDashboard("/hangfire", new DashboardOptions
                {
                    Authorization = [new Infrastructure.HangfireAdminAuthFilter()],
                });

                // Every 15 minutes; see StaleMeetingSweepJob for why meetings can get stuck.
                app.Services.GetRequiredService<IRecurringJobManager>().AddOrUpdate<IStaleMeetingSweepJob>(
                    "sweep-stale-processing-meetings",
                    job => job.RunAsync(CancellationToken.None),
                    "*/15 * * * *");
            }

            await EnsureDatabaseAsync(app.Services, app.Environment);
            await EnsureAdminRoleAsync(app.Services, app.Environment);
            await EnsureBlobStorageAsync(app.Services, app.Environment);

            await app.RunAsync();
        }

        private static async Task EnsureDatabaseAsync(IServiceProvider serviceProvider, IWebHostEnvironment environment)
        {
            if (environment.IsEnvironment("Testing"))
            {
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await dbContext.Database.MigrateAsync();
        }

        // Ensures the "Admin" role exists and promotes any configured bootstrap emails into it.
        // This is the only way to get an Admin today (no promotion UI/endpoint) — needed so
        // *someone* can pass HangfireAdminAuthFilter and open /hangfire.
        private static async Task EnsureAdminRoleAsync(IServiceProvider serviceProvider, IWebHostEnvironment environment)
        {
            if (environment.IsEnvironment("Testing"))
            {
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            var bootstrapEmails = scope.ServiceProvider.GetRequiredService<IOptions<AdminBootstrapOptions>>().Value.Emails;

            if (!await roleManager.RoleExistsAsync("Admin"))
            {
                await roleManager.CreateAsync(new IdentityRole("Admin"));
                logger.LogInformation("Created \"Admin\" role");
            }

            foreach (var email in bootstrapEmails)
            {
                var user = await userManager.FindByEmailAsync(email);
                if (user is null)
                {
                    logger.LogWarning("AdminBootstrap email {Email} does not match any user yet; skipping", email);
                    continue;
                }

                if (!await userManager.IsInRoleAsync(user, "Admin"))
                {
                    await userManager.AddToRoleAsync(user, "Admin");
                    logger.LogInformation("Promoted {Email} to Admin", email);
                }
            }
        }

        // MinIO (unlike AWS S3) does not auto-create buckets — do it once at startup.
        private static async Task EnsureBlobStorageAsync(IServiceProvider serviceProvider, IWebHostEnvironment environment)
        {
            if (environment.IsEnvironment("Testing"))
            {
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

            try
            {
                await blobStorage.EnsureBucketExistsAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Non-fatal: recording just won't work until MinIO/S3 is reachable, but the
                // rest of the app (calls, chat, etc.) shouldn't be blocked from starting.
                logger.LogWarning(ex, "Could not verify/create the blob storage bucket at startup");
            }
        }
    }

    public partial class Program
    {
    }
}
