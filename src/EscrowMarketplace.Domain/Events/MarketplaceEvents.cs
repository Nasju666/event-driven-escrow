namespace EscrowMarketplace.Domain.Events;

public interface IDomainEvent
{
    Guid EventId => Guid.NewGuid();
    DateTimeOffset OccurredOn => DateTimeOffset.UtcNow;
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
    string Currency
) : IDomainEvent;

public record EscrowFundsAuthorizedEvent(
    Guid EscrowAccountId,
    Guid BookingId,
    Guid TenantId,
    decimal TotalAmount,
    string PaymentReference
) : IDomainEvent;

public record EscrowFundsCapturedEvent(
    Guid EscrowAccountId,
    Guid BookingId,
    Guid TenantId,
    decimal TotalAmount,
    DateTimeOffset InspectionDeadline
) : IDomainEvent;

public record ProviderConfirmedBookingEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ProviderId
) : IDomainEvent;

public record ServiceCompletedEvent(
    Guid BookingId,
    Guid TenantId
) : IDomainEvent;

public record EscrowPayoutReleasedEvent(
    Guid EscrowAccountId,
    Guid BookingId,
    Guid TenantId,
    Guid ProviderId,
    decimal ProviderPayoutAmount,
    decimal PlatformCommissionAmount
) : IDomainEvent;

public record BookingCancelledEvent(
    Guid BookingId,
    Guid TenantId,
    Guid ResourceId,
    string Reason
) : IDomainEvent;

public record EscrowDisputeRaisedEvent(
    Guid DisputeId,
    Guid EscrowAccountId,
    Guid BookingId,
    Guid TenantId,
    Guid RaisedByUserId,
    string Reason
) : IDomainEvent;

public record EscrowDisputeResolvedEvent(
    Guid DisputeId,
    Guid EscrowAccountId,
    Guid BookingId,
    Guid TenantId,
    string Resolution
) : IDomainEvent;

public record EscrowStatusChangedIntegrationEvent(
    Guid BookingId,
    Guid EscrowAccountId,
    Guid TenantId,
    Guid BuyerId,
    Guid ProviderId,
    string PreviousStatus,
    string NewStatus,
    string Message,
    DateTimeOffset Timestamp
);
