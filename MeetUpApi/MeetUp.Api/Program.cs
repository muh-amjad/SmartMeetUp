
using MeetUp.Api.Data;
using MeetUp.Api.Entities;
using MeetUp.Api.Options;
using MeetUp.Api.Repositories;
using MeetUp.Api.Services;
using MeetUp.Api.Infrastructure.Middleware;
using MeetUp.Api.Infrastructure.Logging;
using FluentValidation;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
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
                    .Enrich.WithProperty("Environment", context.HostingEnvironment.EnvironmentName);
            });

            builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
            builder.Services.Configure<MeetingOptions>(builder.Configuration.GetSection(MeetingOptions.Section));
            builder.Services.Configure<LiveKitOptions>(builder.Configuration.GetSection(LiveKitOptions.SectionName));
            builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection(BlobStorageOptions.SectionName));
            builder.Services.Configure<AdminBootstrapOptions>(builder.Configuration.GetSection(AdminBootstrapOptions.SectionName));

            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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

            builder.Services.AddScoped<ITokenService, TokenService>();
            builder.Services.AddSingleton<IPresenceTracker, InMemoryPresenceTracker>();
            builder.Services.AddSingleton<ILiveKitService, LiveKitService>();
            builder.Services.AddScoped<IBlobStorageService, S3BlobStorageService>();
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
                    Title = "MeetUp API",
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

            builder.Services.AddCors(options =>
            {
                options.AddPolicy("AllowAngular",
                    policy =>
                    {
                        policy.WithOrigins("http://localhost:4200")
                            .AllowAnyHeader()
                            .AllowAnyMethod()
                            .AllowCredentials();
                    });
            });

            builder.Services.AddSignalR();

            var app = builder.Build();

            app.UseExceptionHandler();

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
