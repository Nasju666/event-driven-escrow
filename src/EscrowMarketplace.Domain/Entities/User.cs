using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Entities;

public class User : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Buyer;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Simulated stored balance. Payments are drawn from it and payouts/refunds are credited to it,
    /// so the books always balance: buyer debit = escrow hold = provider payout + platform fee, or refund.
    /// </summary>
    public decimal WalletBalance { get; set; }

    /// <summary>xmin concurrency token, so two concurrent payments can't both spend the same balance.</summary>
    public uint RowVersion { get; set; }

    public void Debit(decimal amount)
    {
        if (amount <= 0)
            throw new DomainException("Debit amount must be greater than zero.");
        if (WalletBalance < amount)
            throw new InsufficientFundsException(WalletBalance, amount);

        WalletBalance -= amount;
    }

    public void Credit(decimal amount)
    {
        if (amount <= 0)
            throw new DomainException("Credit amount must be greater than zero.");

        WalletBalance += amount;
    }
}
