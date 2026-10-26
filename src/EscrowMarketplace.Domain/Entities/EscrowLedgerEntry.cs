using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;

namespace EscrowMarketplace.Domain.Entities;

public class EscrowLedgerEntry : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid EscrowAccountId { get; set; }
    public LedgerEntryType EntryType { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Description { get; set; } = string.Empty;

    // Navigation
    public EscrowAccount? EscrowAccount { get; set; }
}
