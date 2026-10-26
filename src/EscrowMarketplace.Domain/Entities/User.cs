using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;

namespace EscrowMarketplace.Domain.Entities;

public class User : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Buyer;
    public bool IsActive { get; set; } = true;
    public decimal WalletBalance { get; set; } = 0m;
}
