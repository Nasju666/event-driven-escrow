using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.ValueObjects;

namespace EscrowMarketplace.Domain.Entities;

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

    /// <summary>
    /// PostgreSQL xmin system column mapped concurrency token.
    /// Prevents lost updates when modifying active bookings.
    /// </summary>
    public uint RowVersion { get; set; }

    // Navigations
    public BookableResource? Resource { get; set; }
    public User? Buyer { get; set; }
    public User? Provider { get; set; }
    public EscrowAccount? EscrowAccount { get; set; }

    public void MarkHeld()
    {
        if (Status != BookingStatus.PendingPayment)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Held));

        Status = BookingStatus.Held;
    }

    public void Confirm()
    {
        if (Status != BookingStatus.Held && Status != BookingStatus.PendingPayment)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Confirmed));

        Status = BookingStatus.Confirmed;
    }

    public void StartService()
    {
        if (Status != BookingStatus.Confirmed)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.InService));

        Status = BookingStatus.InService;
    }

    public void Complete()
    {
        if (Status != BookingStatus.Held && Status != BookingStatus.Confirmed && Status != BookingStatus.InService)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Completed));

        Status = BookingStatus.Completed;
    }

    public void Cancel()
    {
        if (Status == BookingStatus.Completed || Status == BookingStatus.Refunded)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Cancelled));

        Status = BookingStatus.Cancelled;
    }

    public void MarkDisputed()
    {
        if (Status != BookingStatus.Held && Status != BookingStatus.Confirmed && Status != BookingStatus.InService && Status != BookingStatus.Completed)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Disputed));

        Status = BookingStatus.Disputed;
    }

    public void Refund()
    {
        if (Status != BookingStatus.Cancelled && Status != BookingStatus.Disputed && Status != BookingStatus.Held)
            throw new InvalidStateTransitionException(nameof(Booking), Status.ToString(), nameof(BookingStatus.Refunded));

        Status = BookingStatus.Refunded;
    }
}
