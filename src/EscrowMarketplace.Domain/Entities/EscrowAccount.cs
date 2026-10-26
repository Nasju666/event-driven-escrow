using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Entities;

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

        var commission = Math.Round(totalAmount * (commissionPercent / 100m), 2);
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

    public void MarkAuthorized(string paymentRef)
    {
        if (Status != EscrowStatus.PendingAuthorization)
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), nameof(EscrowStatus.Authorized));

        Status = EscrowStatus.Authorized;
        PaymentReference = paymentRef;
        AuthorizedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFunded(DateTimeOffset inspectionDeadline)
    {
        if (Status != EscrowStatus.Authorized && Status != EscrowStatus.PendingAuthorization)
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), nameof(EscrowStatus.Funded));

        Status = EscrowStatus.Funded;
        FundedAt = DateTimeOffset.UtcNow;
        InspectionDeadlineUtc = inspectionDeadline;

        // Auto record initial credit ledger entry
        LedgerEntries.Add(new EscrowLedgerEntry
        {
            TenantId = TenantId,
            EscrowAccountId = Id,
            EntryType = LedgerEntryType.HoldDeposit,
            Amount = TotalAmount,
            Currency = Currency,
            Description = $"Escrow funds securely deposited by Buyer {BuyerId} for Booking {BookingId}"
        });
    }

    public void ReleasePayout()
    {
        if (Status != EscrowStatus.Funded)
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), nameof(EscrowStatus.Settled));

        Status = EscrowStatus.Settled;
        SettledAt = DateTimeOffset.UtcNow;

        // Record provider disbursement
        LedgerEntries.Add(new EscrowLedgerEntry
        {
            TenantId = TenantId,
            EscrowAccountId = Id,
            EntryType = LedgerEntryType.ProviderPayout,
            Amount = ProviderPayoutAmount,
            Currency = Currency,
            Description = $"Disbursed payout to Provider {ProviderId}"
        });

        // Record platform commission fee
        LedgerEntries.Add(new EscrowLedgerEntry
        {
            TenantId = TenantId,
            EscrowAccountId = Id,
            EntryType = LedgerEntryType.PlatformFee,
            Amount = PlatformCommissionAmount,
            Currency = Currency,
            Description = $"Platform fee captured ({PlatformCommissionPercent}%)"
        });
    }

    public void Refund(string reason)
    {
        if (Status != EscrowStatus.Funded && Status != EscrowStatus.Authorized && Status != EscrowStatus.Disputed)
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), nameof(EscrowStatus.Refunded));

        Status = EscrowStatus.Refunded;
        RefundedAt = DateTimeOffset.UtcNow;

        LedgerEntries.Add(new EscrowLedgerEntry
        {
            TenantId = TenantId,
            EscrowAccountId = Id,
            EntryType = LedgerEntryType.RefundToBuyer,
            Amount = TotalAmount,
            Currency = Currency,
            Description = $"Full refund disbursed to Buyer {BuyerId}. Reason: {reason}"
        });
    }

    public void MarkDisputed()
    {
        if (Status != EscrowStatus.Funded)
            throw new InvalidStateTransitionException(nameof(EscrowAccount), Status.ToString(), nameof(EscrowStatus.Disputed));

        Status = EscrowStatus.Disputed;
    }
}
