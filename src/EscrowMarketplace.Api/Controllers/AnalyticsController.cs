using EscrowMarketplace.Application.Common.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace EscrowMarketplace.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalyticsController : ControllerBase
{
    private readonly IAnalyticsRepository _analyticsRepository;
    private readonly ICurrentTenantService _tenantService;

    public AnalyticsController(
        IAnalyticsRepository analyticsRepository,
        ICurrentTenantService tenantService)
    {
        _analyticsRepository = analyticsRepository;
        _tenantService = tenantService;
    }

    [HttpGet("reconciliation")]
    [ProducesResponseType(typeof(LedgerReconciliationSummary), StatusCodes.Status200OK)]
    public async Task<ActionResult<LedgerReconciliationSummary>> GetReconciliation(
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantService.TenantId ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        var from = fromUtc ?? DateTimeOffset.UtcNow.AddMonths(-1);
        var to = toUtc ?? DateTimeOffset.UtcNow.AddDays(1);

        var summary = await _analyticsRepository.GetLedgerReconciliationAsync(tenantId, from, to, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("trends")]
    [ProducesResponseType(typeof(IReadOnlyList<DailySettlementMetric>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<DailySettlementMetric>>> GetTrends(
        [FromQuery] int days = 30,
        CancellationToken cancellationToken = default)
    {
        var tenantId = _tenantService.TenantId ?? Guid.Parse("11111111-1111-1111-1111-111111111111");
        var trends = await _analyticsRepository.GetDailySettlementTrendsAsync(tenantId, days, cancellationToken);
        return Ok(trends);
    }
}
