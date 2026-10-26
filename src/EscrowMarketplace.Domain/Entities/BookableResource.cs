using EscrowMarketplace.Domain.Common;

namespace EscrowMarketplace.Domain.Entities;

public class BookableResource : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid ProviderId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal HourlyRate { get; set; }
    public string Currency { get; set; } = "USD";
    public string TimeZone { get; set; } = "UTC";
    public bool IsActive { get; set; } = true;

    // Navigation
    public User? Provider { get; set; }
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
}
