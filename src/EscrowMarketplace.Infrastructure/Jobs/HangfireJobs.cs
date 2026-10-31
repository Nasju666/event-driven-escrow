using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Jobs;

/// <summary>
/// Nightly ledger reconciliation per tenant: a calendar-style batch job, which is what Hangfire is
/// good at. Per-booking deadlines (payment, provider response, auto-release) are NOT polled here;
/// the escrow saga schedules an exact timeout message for each one.
/// </summary>
public class LedgerReconciliationJob
{
    private readonly MarketplaceDbContext _context;
    private readonly IAnalyticsRepository _analyticsRepository;
    private readonly ILogger<LedgerReconciliationJob> _logger;

    public LedgerReconciliationJob(
        MarketplaceDbContext context,
        IAnalyticsRepository analyticsRepository,
        ILogger<LedgerReconciliationJob> logger)
    {
        _context = context;
        _analyticsRepository = analyticsRepository;
        _logger = logger;
    }

    public async Task RunDailyReconciliationAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting nightly ledger reconciliation...");

        var tenants = await _context.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;

        foreach (var tenant in tenants)
        {
            try
            {
                var s = await _analyticsRepository.GetLedgerReconciliationAsync(tenant.Id, now.AddDays(-1), now, cancellationToken);
                _logger.LogInformation(
                    "Tenant {Tenant}: held {Held} across {Holds} escrows, disbursed {Disbursed}, fees {Fees}, refunded {Refunded}, open disputes {Disputes}",
                    tenant.Name, s.TotalHeldInEscrow, s.ActiveEscrowHoldsCount, s.TotalDisbursedToProviders,
                    s.TotalPlatformFeesCaptured, s.TotalRefundedToBuyers, s.ActiveDisputesCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation failed for tenant {TenantId}", tenant.Id);
            }
        }

        _logger.LogInformation("Nightly ledger reconciliation finished.");
    }
}
