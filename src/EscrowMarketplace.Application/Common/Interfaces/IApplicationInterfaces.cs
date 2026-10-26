using EscrowMarketplace.Domain.Entities;
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

public interface ICurrentTenantService
{
    Guid? TenantId { get; }
    string? TenantIdentifier { get; }
    void SetTenant(Guid tenantId, string identifier);
}

public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}

public interface IEscrowPaymentGateway
{
    Task<(bool Success, string TransactionId, string? ErrorMessage)> AuthorizePaymentAsync(
        Guid buyerId, decimal amount, string currency, CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> CapturePaymentAsync(
        string transactionId, decimal amount, CancellationToken cancellationToken = default);

    Task<(bool Success, string? ErrorMessage)> RefundPaymentAsync(
        string transactionId, decimal amount, string reason, CancellationToken cancellationToken = default);
}

public interface IBookingClient
{
    Task BookingCreated(object bookingDto);
    Task BookingStatusChanged(string bookingId, string status, string message);
    Task EscrowStatusChanged(string escrowId, string status, decimal amount);
    Task AvailabilityChanged(string resourceId, DateTimeOffset start, DateTimeOffset end);
}

public interface IBookingNotifier
{
    Task NotifyBookingCreatedAsync(Guid tenantId, Guid bookingId, object payload, CancellationToken cancellationToken = default);
    Task NotifyStatusChangedAsync(Guid tenantId, Guid bookingId, string status, string message, CancellationToken cancellationToken = default);
    Task NotifyEscrowTransitionAsync(Guid tenantId, Guid escrowId, string status, decimal amount, CancellationToken cancellationToken = default);
}

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
