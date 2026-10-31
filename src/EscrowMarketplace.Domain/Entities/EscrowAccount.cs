using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Entities;

/// <summary>
/// Escrow aggregate. Holds the buyer's money between payment and settlement and records
/// every movement in an append-only ledger:
/// PendingAuthorization → Authorized → Funded → Settled | Refunded (via Disputed if contested),
/// or Voided when the booking ends before any money moved.
/// </summary>
public class EscrowAccount : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid BookingId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid ProviderId { get; set; }

    public decimal TotalAmount { get; set; }
    public decimal PlatformCommissionPercent { get; set; }
    public decimal PlatformCommissionAmount { get; set; }
    public decimal ProviderPayoutAmount { get; set; }
    public string Currency { get; set; } = "USD";

    public EscrowStatus Status { get; set; } = EscrowStatus.PendingAuthorization;
    public string? PaymentReference { get; set; }

    public DateTimeOffset? AuthorizedAt { get; set; }
    public DateTimeOffset? FundedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public DateTimeOffset? RefundedAt { get; set; }
    public DateTimeOffset? InspectionDeadlineUtc { get; set; }

    public uint RowVersion { get; set; }

    // Navigations
    public Booking? Booking { get; set; }
    public User? Buyer { get; set; }
    public User? Provider { get; set; }
    public ICollection<EscrowLedgerEntry> LedgerEntries { get; set; } = new List<EscrowLedgerEntry>();
    public ICollection<Dispute> Disputes { get; set; } = new List<Dispute>();

    public static EscrowAccount Create(
        Guid tenantId,
        Guid bookingId,
        Guid buyerId,
        Guid providerId,
        decimal totalAmount,
        decimal commissionPercent,
        string currency = "USD")
    {
        if (totalAmount <= 0)
            throw new ArgumentException("Escrow total amount must be greater than zero.", nameof(totalAmount));

        if (commissionPercent < 0 || commissionPercent > 100)
            throw new ArgumentException("Commission percent must be between 0 and 100.", nameof(commissionPercent));

        var commission = Math.Round(totalAmount * (commissionPercent / 100m), 2, MidpointRounding.AwayFromZero);
        var payout = totalAmount - commission;

        return new EscrowAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            BookingId = bookingId,
            BuyerId = buyerId,
            ProviderId = providerId,
            TotalAmount = totalAmount,
            PlatformCommissionPercent = commissionPercent,
            PlatformCommissionAmount = commission,
            ProviderPayoutAmount = payout,
            Currency = currency,
            Status = EscrowStatus.PendingAuthorization
        };
    }

    public void Authorize(string paymentReference)
    {
        TransitionTo(EscrowStatus.Authorized, EscrowStatus.PendingAuthorization);
        PaymentReference = paymentReference;
        AuthorizedAt = DateTimeOffset.UtcNow;
        AddDomainEvent(new EscrowFundsAuthorizedEvent(BookingId, TenantId, Id, TotalAmount, paymentReference));
    }

    public void Capture()
    {
        TransitionTo(EscrowStatus.Funded, EscrowStatus.Authorized);
        FundedAt = DateTimeOffset.UtcNow;

        AddLedgerEntry(LedgerEntryType.HoldDeposit, TotalAmount,
            $"Buyer {BuyerId} deposited funds into escrow for booking {BookingId}");

        AddDomainEvent(new EscrowFundsCapturedEvent(BookingId, TenantId, Id, TotalAmount));
    }

    public void StartInspectionWindow(DateTimeOffset deadlineUtc)
    {
        if (Status != EscrowStatus.Funded)
            throw new DomainException($"Inspection window can only start on a funded escrow (current: {Status}).");

        InspectionDeadlineUtc = deadlineUtc;
    }

    public void MarkDisputed()
    {
        TransitionTo(EscrowStatus.Disputed, EscrowStatus.Funded);
    }

    /// <summary>Releases the held funds: payout to the provider, commission to the platform.</summary>
    public void ReleasePayout()
    {
        TransitionTo(EscrowStatus.Settled, EscrowStatus.Funded, EscrowStatus.Disputed);
        SettledAt = DateTimeOffset.UtcNow;

        AddLedgerEntry(LedgerEntryType.ProviderPayout, ProviderPayoutAmount,
            $"Payout released to provider {ProviderId}");
        AddLedgerEntry(LedgerEntryType.PlatformFee, PlatformCommissionAmount,
            $"Platform fee captured ({PlatformCommissionPercent}%)");

        AddDomainEvent(new EscrowPayoutReleasedEvent(
            BookingId, TenantId, Id, ProviderId, ProviderPayoutAmount, PlatformCommissionAmount));
    }

    /// <summary>Returns the full held amount to the buyer.</summary>
    public void Refund(string reason)
    {
        TransitionTo(EscrowStatus.Refunded, EscrowStatus.Funded, EscrowStatus.Disputed);
        RefundedAt = DateTimeOffset.UtcNow;

        AddLedgerEntry(LedgerEntryType.RefundToBuyer, TotalAmount,
            $"Full refund to buyer {BuyerId}. Reason: {reason}");

        AddDomainEvent(new EscrowRefundedEvent(BookingId, TenantId, Id, BuyerId, TotalAmount, reason));
    }

    /// <summary>Closes an escrow that never received money. No ledger entry, because nothing moved.</summary>
    public void Void()
    {
        TransitionTo(EscrowStatus.Voided, EscrowStatus.PendingAuthorization, EscrowStatus.Authorized);
    }

    private void AddLedgerEntry(LedgerEntryType type, decimal amount, string description)
    {
        // No Id here: entries reach EF through the navigation, and a preset key would make EF
        // treat them as existing rows (UPDATE instead of INSERT). EF generates the key on add.
        LedgerEntries.Add(new EscrowLedgerEntry
        {
            TenantId = TenantId,
            EscrowAccountId = Id,
            EntryType = type,
            Amount = amount,
            Currency = Currency,
            Description = description
        });
    }

    private void TransitionTo(EscrowStatus target, params EscrowStatus[] allowedFrom)
    {
        if (!allowedFrom.Contains(Status))
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), target.ToString());

        Status = target;
    }
}
