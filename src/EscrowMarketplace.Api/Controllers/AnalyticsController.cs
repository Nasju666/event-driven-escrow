using EscrowMarketplace.Api.Authentication;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace EscrowMarketplace.Api.Controllers;

/// <summary>Tenant-wide money figures (Dapper aggregates over the escrow table). Admins only.</summary>
[ApiController]
[Route("api/analytics")]
[Authorize(Policy = Policies.Admin)]
[EnableRateLimiting(RateLimitPolicies.PerUser)]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsRepository _analyticsRepository;
    private readonly ICurrentTenantService _tenantService;

    public AnalyticsController(IAnalyticsRepository analyticsRepository, ICurrentTenantService tenantService)
    {
        _analyticsRepository = analyticsRepository;
        _tenantService = tenantService;
    }

    [HttpGet("reconciliation")]
    public async Task<ActionResult<LedgerReconciliationSummary>> GetReconciliation(
        [FromQuery] DateTimeOffset? fromUtc, [FromQuery] DateTimeOffset? toUtc, CancellationToken ct = default)
    {
        var tenantId = _tenantService.RequireTenantId();
        var from = fromUtc ?? DateTimeOffset.UtcNow.AddDays(-30);
        var to = toUtc ?? DateTimeOffset.UtcNow.AddDays(1);
        return Ok(await _analyticsRepository.GetLedgerReconciliationAsync(tenantId, from, to, ct));
    }

    [HttpGet("trends")]
    public async Task<ActionResult<IReadOnlyList<DailySettlementMetric>>> GetTrends([FromQuery] int days = 30, CancellationToken ct = default)
    {
        var tenantId = _tenantService.RequireTenantId();
        return Ok(await _analyticsRepository.GetDailySettlementTrendsAsync(tenantId, Math.Clamp(days, 1, 365), ct));
    }
}
