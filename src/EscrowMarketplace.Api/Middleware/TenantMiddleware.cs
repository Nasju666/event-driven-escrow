using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Api.Middleware;

public class TenantMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TenantMiddleware> _logger;

    public TenantMiddleware(RequestDelegate next, ILogger<TenantMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICurrentTenantService tenantService, MarketplaceDbContext dbContext)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;

        // Skip tenant check for swagger, scalar, openapi, health endpoints, root, or static files
        if (path.StartsWith("/swagger") || path.StartsWith("/scalar") || path.StartsWith("/openapi") || path.StartsWith("/health") || path.StartsWith("/hangfire") || path == "/" || path.Contains("."))
        {
            await _next(context);
            return;
        }

        string? tenantHeader = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        // Also allow query string for WebSocket handshakes / testing
        if (string.IsNullOrEmpty(tenantHeader))
        {
            tenantHeader = context.Request.Query["X-Tenant-Id"].FirstOrDefault()
                ?? context.Request.Query["tenant_id"].FirstOrDefault();
        }

        // Also check JWT claim if present
        if (string.IsNullOrEmpty(tenantHeader) && context.User.Identity?.IsAuthenticated == true)
        {
            tenantHeader = context.User.FindFirst("tenant_id")?.Value;
        }

        if (!string.IsNullOrEmpty(tenantHeader))
        {
            if (Guid.TryParse(tenantHeader, out var tenantGuid))
            {
                var tenant = await dbContext.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Id == tenantGuid && t.IsActive);

                if (tenant != null)
                {
                    tenantService.SetTenant(tenant.Id, tenant.Identifier);
                }
            }
            else
            {
                // Fallback to identifier search (e.g. "apex-studios")
                var tenant = await dbContext.Tenants
                    .AsNoTracking()
                    .FirstOrDefaultAsync(t => t.Identifier == tenantHeader && t.IsActive);

                if (tenant != null)
                {
                    tenantService.SetTenant(tenant.Id, tenant.Identifier);
                }
            }
        }

        await _next(context);
    }
}
