namespace EscrowMarketplace.Domain.Events;

/// <summary>
/// Marker for events raised by aggregates. Every event carries the BookingId so the escrow
/// saga can correlate it, and the TenantId so consumers can restore tenant context.
/// </summary>
public interface IDomainEvent
{
    Guid BookingId { get; }
    Guid TenantId { get; }
}

public record BookingSlotReservedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    Guid BuyerId,
    Guid ProviderId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PaymentDueUtc
) : IDomainEvent;

public record EscrowFundsAuthorizedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid EscrowAccountId,
    decimal TotalAmount,
    string PaymentReference
) : IDomainEvent;

public record EscrowFundsCapturedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid EscrowAccountId,
    decimal TotalAmount
) : IDomainEvent;

/// <summary>Buyer paid. The provider has until ProviderResponseDueUtc to accept.</summary>
public record BookingPaidEvent(
    Guid BookingId,
    Guid TenantId,
    DateTimeOffset ProviderResponseDueUtc
) : IDomainEvent;

public record ProviderConfirmedBookingEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ProviderId
) : IDomainEvent;

/// <summary>Provider marked the session done. The buyer can dispute until InspectionDeadlineUtc.</summary>
public record ServiceCompletedEvent(
    Guid BookingId,
    Guid TenantId,
    DateTimeOffset InspectionDeadlineUtc
) : IDomainEvent;

public record EscrowPayoutReleasedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid EscrowAccountId,
    Guid ProviderId,
    decimal ProviderPayoutAmount,
    decimal PlatformCommissionAmount
) : IDomainEvent;

public record EscrowRefundedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid EscrowAccountId,
    Guid BuyerId,
    decimal Amount,
    string Reason
) : IDomainEvent;

public record BookingCancelledEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    string Reason
) : IDomainEvent;

public record BookingExpiredEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId
) : IDomainEvent;

public record EscrowDisputeRaisedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid DisputeId,
    Guid EscrowAccountId,
    Guid RaisedByUserId,
    string Reason
) : IDomainEvent;

public record EscrowDisputeResolvedEvent(
    Guid BookingId,
    Guid TenantId,
    Guid DisputeId,
    Guid EscrowAccountId,
    string Resolution
) : IDomainEvent;
