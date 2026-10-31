using Dapper;
using EscrowMarketplace.Application.Common.Interfaces;
using Microsoft.EntityFrameworkCore;
using EscrowMarketplace.Infrastructure.Persistence;

namespace EscrowMarketplace.Infrastructure.Repositories;

public class DapperAnalyticsRepository : IAnalyticsRepository
{
    private readonly MarketplaceDbContext _context;

    public DapperAnalyticsRepository(MarketplaceDbContext context)
    {
        _context = context;
    }

    public async Task<LedgerReconciliationSummary> GetLedgerReconciliationAsync(
        Guid tenantId,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var connection = _context.Database.GetDbConnection();

        // EscrowStatus: 2 = Funded, 3 = Settled, 4 = Refunded, 5 = Disputed.
        // Money is actually held while Funded or Disputed (Authorized is a transient step).
        const string sql = @"
            SELECT
                COALESCE(SUM(CASE WHEN e.""Status"" IN (2, 5) THEN e.""TotalAmount"" ELSE 0 END), 0) AS TotalHeldInEscrow,
                COALESCE(SUM(CASE WHEN e.""Status"" = 3 THEN e.""ProviderPayoutAmount"" ELSE 0 END), 0) AS TotalDisbursedToProviders,
                COALESCE(SUM(CASE WHEN e.""Status"" = 3 THEN e.""PlatformCommissionAmount"" ELSE 0 END), 0) AS TotalPlatformFeesCaptured,
                COALESCE(SUM(CASE WHEN e.""Status"" = 4 THEN e.""TotalAmount"" ELSE 0 END), 0) AS TotalRefundedToBuyers,
                COUNT(CASE WHEN e.""Status"" IN (2, 5) THEN 1 END) AS ActiveEscrowHoldsCount,
                COUNT(CASE WHEN e.""Status"" = 3 THEN 1 END) AS SettledBookingsCount,
                COUNT(CASE WHEN e.""Status"" = 5 THEN 1 END) AS ActiveDisputesCount
            FROM ""EscrowAccounts"" e
            WHERE e.""TenantId"" = @TenantId
              AND e.""CreatedAt"" >= @FromUtc
              AND e.""CreatedAt"" <= @ToUtc;";

        var result = await connection.QueryFirstOrDefaultAsync<dynamic>(
            new CommandDefinition(sql, new { TenantId = tenantId, FromUtc = fromUtc, ToUtc = toUtc }, cancellationToken: cancellationToken));

        if (result == null)
        {
            return new LedgerReconciliationSummary(tenantId, 0m, 0m, 0m, 0m, 0, 0, 0);
        }

        return new LedgerReconciliationSummary(
            TenantId: tenantId,
            TotalHeldInEscrow: (decimal)(result.totalheldinescrow ?? 0m),
            TotalDisbursedToProviders: (decimal)(result.totaldisbursedtoproviders ?? 0m),
            TotalPlatformFeesCaptured: (decimal)(result.totalplatformfeescaptured ?? 0m),
            TotalRefundedToBuyers: (decimal)(result.totalrefundedtobuyers ?? 0m),
            ActiveEscrowHoldsCount: (int)(result.activeescrowholdscount ?? 0),
            SettledBookingsCount: (int)(result.settledbookingscount ?? 0),
            ActiveDisputesCount: (int)(result.activedisputescount ?? 0)
        );
    }

    public async Task<IReadOnlyList<DailySettlementMetric>> GetDailySettlementTrendsAsync(
        Guid tenantId,
        int days,
        CancellationToken cancellationToken = default)
    {
        var connection = _context.Database.GetDbConnection();
        var since = DateTimeOffset.UtcNow.AddDays(-days);

        const string sql = @"
            SELECT 
                CAST(e.""CreatedAt"" AS DATE) AS Date,
                COUNT(*) AS BookingsCount,
                COALESCE(SUM(e.""TotalAmount""), 0) AS VolumeGross,
                COALESCE(SUM(e.""PlatformCommissionAmount""), 0) AS NetPlatformFees
            FROM ""EscrowAccounts"" e
            WHERE e.""TenantId"" = @TenantId
              AND e.""CreatedAt"" >= @Since
            GROUP BY CAST(e.""CreatedAt"" AS DATE)
            ORDER BY Date ASC;";

        var results = await connection.QueryAsync<dynamic>(
            new CommandDefinition(sql, new { TenantId = tenantId, Since = since }, cancellationToken: cancellationToken));

        var list = new List<DailySettlementMetric>();
        foreach (var r in results)
        {
            DateTime dt = (DateTime)r.date;
            list.Add(new DailySettlementMetric(
                Date: DateOnly.FromDateTime(dt),
                BookingsCount: (int)(r.bookingscount ?? 0),
                VolumeGross: (decimal)(r.volumegross ?? 0m),
                NetPlatformFees: (decimal)(r.netplatformfees ?? 0m)
            ));
        }

        return list;
    }
}
