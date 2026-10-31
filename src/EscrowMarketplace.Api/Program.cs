using System.Security.Claims;
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
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging ──
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}"));

// ── Application & infrastructure ──
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices(builder.Configuration);

// ── Authentication: JWT bearer (query-string token accepted only for the SignalR hub) ──
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret) || Encoding.UTF8.GetByteCount(jwtSecret) < 32)
    throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes long.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
builder.Services.AddSingleton<IPostConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

// ── Authorization: authenticated by default, roles on top ──
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy(Policies.Buyer, p => p.RequireRole(Policies.BuyerRole));
    options.AddPolicy(Policies.Provider, p => p.RequireRole(Policies.ProviderRole));
    options.AddPolicy(Policies.Admin, p => p.RequireRole(Policies.AdminRoles));
});

// ── Rate limiting: per user, plus a concurrency cap on checkout per tenant ──
var requestsPerMinute = builder.Configuration.GetValue("RateLimiting:RequestsPerMinutePerUser", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy(RateLimitPolicies.PerUser, httpContext =>
        RateLimitPartition.GetSlidingWindowLimiter(UserPartition(httpContext), _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = requestsPerMinute,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0
        }));

    options.AddPolicy(RateLimitPolicies.Checkout, httpContext =>
        RateLimitPartition.GetConcurrencyLimiter(TenantPartition(httpContext), _ => new ConcurrencyLimiterOptions
        {
            PermitLimit = 20,
            QueueLimit = 50
        }));
});

// ── MVC, enums as strings, OpenAPI ──
builder.Services.AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddOpenApi();

var app = builder.Build();

// ── Schema, exclusion constraint and demo seed ──
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<MarketplaceDbContextInitializer>();
    await initializer.InitializeAsync();
}

// ── Pipeline ──
app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMiddleware>(); // tenant + actor come from the validated JWT only
app.UseRateLimiter();

if (app.Configuration.GetValue("ApiDocs:Enabled", true))
{
    app.MapOpenApi().AllowAnonymous();
    app.MapScalarApiReference(options =>
    {
        options.Title = "Escrow & Booking Marketplace API";
        options.Theme = ScalarTheme.Purple;
    }).AllowAnonymous();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.MapHub<MarketplaceBookingHub>("/hubs/marketplace", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

if (EscrowMarketplace.Infrastructure.DependencyInjection.IsHangfireEnabled(app.Configuration))
{
    // Dashboard is local-requests-only by default (Hangfire's LocalRequestsOnlyAuthorizationFilter).
    app.UseHangfireDashboard("/hangfire", new DashboardOptions { DashboardTitle = "Escrow Marketplace Jobs" });

    RecurringJob.AddOrUpdate<LedgerReconciliationJob>(
        "nightly-ledger-reconciliation",
        job => job.RunDailyReconciliationAsync(CancellationToken.None),
        Cron.Daily(2));

    // Replaced by saga timeouts; remove the old polling job if an earlier version registered it.
    RecurringJob.RemoveIfExists("escrow-timeout-hold-cleanup");
}

app.MapControllers();

app.Run();

static string UserPartition(HttpContext httpContext) =>
    httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)
    ?? httpContext.Connection.RemoteIpAddress?.ToString()
    ?? "anonymous";

static string TenantPartition(HttpContext httpContext) =>
    httpContext.User.FindFirstValue("tenant_id") ?? "anonymous";

// Required for WebApplicationFactory in integration tests
public partial class Program { }
