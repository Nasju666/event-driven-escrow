using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Common.Interfaces;

public interface IMarketplaceDbContext
{
    DbSet<Tenant> Tenants { get; }
    DbSet<User> Users { get; }
    DbSet<BookableResource> BookableResources { get; }
    DbSet<Booking> Bookings { get; }
    DbSet<EscrowAccount> EscrowAccounts { get; }
    DbSet<EscrowLedgerEntry> EscrowLedgerEntries { get; }
    DbSet<Dispute> Disputes { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IReadDbContext
{
    IQueryable<Tenant> Tenants { get; }
    IQueryable<User> Users { get; }
    IQueryable<BookableResource> BookableResources { get; }
    IQueryable<Booking> Bookings { get; }
    IQueryable<EscrowAccount> EscrowAccounts { get; }
    IQueryable<EscrowLedgerEntry> EscrowLedgerEntries { get; }
    IQueryable<Dispute> Disputes { get; }
}

/// <summary>
/// The tenant for the current request or message. Set from the JWT "tenant_id" claim for HTTP
/// requests and from the message for background consumers. Never from client-supplied headers.
/// </summary>
public interface ICurrentTenantService
{
    Guid? TenantId { get; }
    string? TenantIdentifier { get; }
    void SetTenant(Guid tenantId, string identifier);
}

/// <summary>
/// Who is performing the current operation: an authenticated user (from the JWT) or the
/// system itself (saga timeouts). Handlers use this for authorization, never request bodies.
/// </summary>
public interface ICurrentActor
{
    Guid? UserId { get; }
    UserRole? Role { get; }
    bool IsSystem { get; }
    void SetUser(Guid userId, UserRole role);
    void SetSystem();
}

/// <summary>Reads the escrow saga's persisted state for a booking (null when not available).</summary>
public interface IEscrowWorkflowReader
{
    Task<WorkflowStateDto?> GetAsync(Guid bookingId, CancellationToken cancellationToken = default);
}

public record WorkflowStateDto(
    Guid BookingId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? NextDeadlineUtc,
    string? NextDeadlineKind);

public interface IAnalyticsRepository
{
    Task<LedgerReconciliationSummary> GetLedgerReconciliationAsync(Guid tenantId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DailySettlementMetric>> GetDailySettlementTrendsAsync(Guid tenantId, int days, CancellationToken cancellationToken = default);
}

public record LedgerReconciliationSummary(
    Guid TenantId,
    decimal TotalHeldInEscrow,
    decimal TotalDisbursedToProviders,
    decimal TotalPlatformFeesCaptured,
    decimal TotalRefundedToBuyers,
    int ActiveEscrowHoldsCount,
    int SettledBookingsCount,
    int ActiveDisputesCount
);

public record DailySettlementMetric(
    DateOnly Date,
    int BookingsCount,
    decimal VolumeGross,
    decimal NetPlatformFees
);
