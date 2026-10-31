using System.Security.Claims;
using System.Text.Json;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Api.Middleware;

/// <summary>
/// Resolves the tenant and the acting user for the request from the VALIDATED JWT only.
/// Client-supplied headers such as X-Tenant-Id are deliberately ignored: trusting them let any
/// user read another tenant's data. Runs after UseAuthentication/UseAuthorization, so
/// context.User is populated.
/// Anonymous requests (login, docs, static files) pass through with no tenant set, and the
/// fail-closed query filters then return no tenant data.
/// </summary>
public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentTenantService tenantService,
        ICurrentActor actor,
        MarketplaceDbContext dbContext)
    {
        var user = context.User;

        if (user.Identity?.IsAuthenticated == true)
        {
            var userIdValue = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
            var tenantIdValue = user.FindFirstValue("tenant_id");
            var roleValue = user.FindFirstValue(ClaimTypes.Role);

            if (!Guid.TryParse(userIdValue, out var userId)
                || !Guid.TryParse(tenantIdValue, out var tenantId)
                || !Enum.TryParse<UserRole>(roleValue, out var role))
            {
                _logger.LogWarning("Rejected token with missing or malformed user/tenant/role claims.");
                await WriteProblem(context, StatusCodes.Status401Unauthorized, "Invalid token", "The access token is missing required claims.");
                return;
            }

            var tenant = await dbContext.Tenants
                .AsNoTracking()
                .Where(t => t.Id == tenantId && t.IsActive)
                .Select(t => new { t.Id, t.Identifier })
                .FirstOrDefaultAsync(context.RequestAborted);

            if (tenant is null)
            {
                await WriteProblem(context, StatusCodes.Status403Forbidden, "Tenant unavailable", "The tenant in this token does not exist or is inactive.");
                return;
            }

            tenantService.SetTenant(tenant.Id, tenant.Identifier);
            actor.SetUser(userId, role);
        }

        await _next(context);
    }

    private static async Task WriteProblem(HttpContext context, int status, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail, Instance = context.Request.Path };
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem));
    }
}
