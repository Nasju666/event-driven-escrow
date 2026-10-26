using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Jobs;

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
        _logger.LogInformation("Starting scheduled Hangfire Ledger Reconciliation Job...");

        var tenants = await _context.Tenants.Where(t => t.IsActive).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var yesterday = now.AddDays(-1);

        foreach (var tenant in tenants)
        {
            try
            {
                var summary = await _analyticsRepository.GetLedgerReconciliationAsync(tenant.Id, yesterday, now, cancellationToken);
                _logger.LogInformation(
                    "Tenant [{TenantName}] Ledger: Active Holds: {HoldsCount} (${Held}), Settled: {SettledCount} (Disbursed: ${Disbursed}, Fees: ${Fees}), Disputes: {Disputes}",
                    tenant.Name,
                    summary.ActiveEscrowHoldsCount,
                    summary.TotalHeldInEscrow,
                    summary.SettledBookingsCount,
                    summary.TotalDisbursedToProviders,
                    summary.TotalPlatformFeesCaptured,
                    summary.ActiveDisputesCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reconciliation failed for tenant {TenantId}", tenant.Id);
            }
        }

        _logger.LogInformation("Completed Hangfire Ledger Reconciliation Job.");
    }
}

public class EscrowTimeoutCleanupJob
{
    private readonly MarketplaceDbContext _context;
    private readonly IBookingNotifier _notifier;
    private readonly ILogger<EscrowTimeoutCleanupJob> _logger;

    public EscrowTimeoutCleanupJob(
        MarketplaceDbContext context,
        IBookingNotifier notifier,
        ILogger<EscrowTimeoutCleanupJob> logger)
    {
        _context = context;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task ReleaseExpiredHoldsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Running Escrow Timeout Cleanup Job...");

        var now = DateTimeOffset.UtcNow;
        var expiredHolds = await _context.EscrowAccounts
            .Include(e => e.Booking)
            .Where(e => e.Status == EscrowStatus.PendingAuthorization 
                     && e.CreatedAt < now.AddMinutes(-30))
            .ToListAsync(cancellationToken);

        foreach (var escrow in expiredHolds)
        {
            _logger.LogInformation("Hold expired for Booking {BookingId}. Auto-cancelling hold.", escrow.BookingId);
            if (escrow.Booking != null && escrow.Booking.Status == BookingStatus.Held)
            {
                escrow.Booking.Cancel();
            }

            await _notifier.NotifyStatusChangedAsync(
                escrow.TenantId, escrow.BookingId, "Expired", "Payment authorization timed out. Slot released.", cancellationToken);
        }

        if (expiredHolds.Count > 0)
        {
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Auto-released {Count} expired booking holds.", expiredHolds.Count);
        }
    }
}
