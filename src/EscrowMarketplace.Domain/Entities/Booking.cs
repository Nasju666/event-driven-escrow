using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.ValueObjects;

namespace EscrowMarketplace.Domain.Entities;

/// <summary>
/// Booking aggregate. Owns the reservation lifecycle:
/// PendingPayment → Held → Confirmed → (InService) → Completed → Settled,
/// with exits to Cancelled / Expired / Disputed / Refunded.
/// Every transition raises a domain event that the escrow saga reacts to.
/// </summary>
public class Booking : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid ProviderId { get; set; }

    public TimeRange Period { get; set; } = null!;
    public decimal TotalAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public BookingStatus Status { get; set; } = BookingStatus.PendingPayment;

    public DateTimeOffset PaymentDueUtc { get; set; }
    public DateTimeOffset? ProviderResponseDueUtc { get; set; }
    public string? CancellationReason { get; set; }

    /// <summary>PostgreSQL xmin concurrency token. Prevents lost updates on concurrent transitions.</summary>
    public uint RowVersion { get; set; }

    // Navigations
    public BookableResource? Resource { get; set; }
    public User? Buyer { get; set; }
    public User? Provider { get; set; }
    public EscrowAccount? EscrowAccount { get; set; }

    public static Booking Reserve(
        Guid tenantId,
        BookableResource resource,
        Guid buyerId,
        TimeRange period,
        DateTimeOffset paymentDueUtc)
    {
        if (resource.ProviderId == buyerId)
            throw new DomainException("Providers cannot book their own resources.");

        var hours = (decimal)period.Duration.TotalHours;
        var total = Math.Round(hours * resource.HourlyRate, 2, MidpointRounding.AwayFromZero);
        if (total <= 0)
            throw new DomainException("Booking total must be greater than zero.");

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ResourceId = resource.Id,
            BuyerId = buyerId,
            ProviderId = resource.ProviderId,
            Period = period,
            TotalAmount = total,
            Currency = resource.Currency,
            Status = BookingStatus.PendingPayment,
            PaymentDueUtc = paymentDueUtc
        };

        booking.AddDomainEvent(new BookingSlotReservedEvent(
            booking.Id, tenantId, resource.Id, buyerId, resource.ProviderId,
            period.Start, period.End, total, resource.Currency, paymentDueUtc));

        return booking;
    }

    public bool IsParticipant(Guid userId) => BuyerId == userId || ProviderId == userId;

    public void MarkPaid(DateTimeOffset providerResponseDueUtc)
    {
        TransitionTo(BookingStatus.Held, BookingStatus.PendingPayment);
        ProviderResponseDueUtc = providerResponseDueUtc;
        AddDomainEvent(new BookingPaidEvent(Id, TenantId, providerResponseDueUtc));
    }

    public void Confirm()
    {
        TransitionTo(BookingStatus.Confirmed, BookingStatus.Held);
        AddDomainEvent(new ProviderConfirmedBookingEvent(Id, TenantId, ProviderId));
    }

    public void StartService()
    {
        TransitionTo(BookingStatus.InService, BookingStatus.Confirmed);
    }

    public void Complete(DateTimeOffset inspectionDeadlineUtc)
    {
        TransitionTo(BookingStatus.Completed, BookingStatus.Confirmed, BookingStatus.InService);
        AddDomainEvent(new ServiceCompletedEvent(Id, TenantId, inspectionDeadlineUtc));
    }

    public void Cancel(string reason)
    {
        TransitionTo(BookingStatus.Cancelled,
            BookingStatus.PendingPayment, BookingStatus.Held, BookingStatus.Confirmed);
        CancellationReason = reason;
        AddDomainEvent(new BookingCancelledEvent(Id, TenantId, ResourceId, reason));
    }

    public void Expire()
    {
        TransitionTo(BookingStatus.Expired, BookingStatus.PendingPayment);
        CancellationReason = "Payment window elapsed.";
        AddDomainEvent(new BookingExpiredEvent(Id, TenantId, ResourceId));
    }

    public void MarkDisputed()
    {
        TransitionTo(BookingStatus.Disputed,
            BookingStatus.Confirmed, BookingStatus.InService, BookingStatus.Completed);
    }

    public void MarkRefunded()
    {
        TransitionTo(BookingStatus.Refunded, BookingStatus.Cancelled, BookingStatus.Disputed);
    }

    public void MarkSettled()
    {
        TransitionTo(BookingStatus.Settled, BookingStatus.Completed, BookingStatus.Disputed);
    }

    private void TransitionTo(BookingStatus target, params BookingStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), target.ToString());

        Status = target;
    }
}
