using EscrowMarketplace.Domain.Common;

namespace EscrowMarketplace.Domain.Entities;

public class Tenant : BaseEntity, IAuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string Currency { get; set; } = "USD";
    public decimal DefaultPlatformCommissionPercent { get; set; } = 10.0m;
    public bool IsActive { get; set; } = true;
    public string TimeZone { get; set; } = "UTC";
}
