using System.Text;
using System.Threading.RateLimiting;
using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Api.Middleware;
using EscrowMarketplace.Application;
using EscrowMarketplace.Infrastructure;
using EscrowMarketplace.Infrastructure.Hubs;
using EscrowMarketplace.Infrastructure.Jobs;
using EscrowMarketplace.Infrastructure.Persistence;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// 1. Serilog Structured Logging
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));

// 2. Application & Infrastructure Layers
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// 3. JWT Bearer Authentication & Query-String WebSocket Token Interceptor
var jwtSecret = builder.Configuration["Jwt:Secret"] ?? "SuperSecretEnterpriseEscrowKey2026!MustBeAtLeast32Bytes";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "EscrowMarketplace",
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "EscrowMarketplaceClients",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

// Scoped query-string token extractor for /hubs/marketplace
builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

// 4. ASP.NET Core Flow Control & Rate Limiting (Partitioned by Tenant)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Sliding window for general endpoints
    options.AddPolicy("StandardSlidingWindow", httpContext =>
    {
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "anonymous";
        return RateLimitPartition.GetSlidingWindowLimiter(tenantId, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 100,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 10
        });
    });

    // Concurrency limiter for checkout / booking creation
    options.AddPolicy("CheckoutConcurrencyLimiter", httpContext =>
    {
        var tenantId = httpContext.Request.Headers["X-Tenant-Id"].FirstOrDefault() ?? "anonymous";
        return RateLimitPartition.GetConcurrencyLimiter(tenantId, _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 10,
            QueueLimit = 20
        });
    });
});

// 5. Controllers & OpenAPI / Scalar
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// 6. Database Initialization & Seeding
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MarketplaceDbContextInitializer>();
    await initializer.InitializeAsync();
}

// 7. Middlewares Pipeline
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<TenantMiddleware>();

app.UseSerilogRequestLogging();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// 8. API Documentation (OpenAPI + Scalar)
if (app.Environment.IsDevelopment() || true)
{
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Real-Time Escrow & Booking Marketplace API";
        options.Theme = ScalarTheme.Purple;
        options.DefaultHttpClient = new KeyValuePair<ScalarTarget, ScalarClient>(ScalarTarget.CSharp, ScalarClient.HttpClient);
    });
}

// 9. SignalR Hub Mapping with Authentication Expiration & Refresh Options
app.MapHub<MarketplaceBookingHub>("/hubs/marketplace", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

// 10. Hangfire Dashboard & Scheduled Recurring Jobs
if (!builder.Configuration.GetValue<bool>("UseInMemoryDatabase"))
{
    app.UseHangfireDashboard("/hangfire", new DashboardOptions
    {
        DashboardTitle = "Escrow Marketplace Background Jobs"
    });

    RecurringJob.AddOrUpdate<LedgerReconciliationJob>(
        "nightly-ledger-reconciliation",
        job => job.RunDailyReconciliationAsync(CancellationToken.None),
        Cron.Daily(2)); // Runs daily at 2:00 AM UTC

    RecurringJob.AddOrUpdate<EscrowTimeoutCleanupJob>(
        "escrow-timeout-hold-cleanup",
        job => job.ReleaseExpiredHoldsAsync(CancellationToken.None),
        "*/10 * * * *"); // Runs every 10 minutes
}

app.MapControllers();

app.Logger.LogInformation("Scalar Interactive API Reference available at: http://localhost:5000/scalar/v1 (or http://localhost:5090/scalar/v1)");

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program { }
