using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Entities;

public class Dispute : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid EscrowAccountId { get; set; }
    public Guid RaisedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string EvidenceSummary { get; set; } = string.Empty;
    public DisputeStatus Status { get; set; } = DisputeStatus.PendingReview;
    public string? ResolutionNotes { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    // Navigations
    public EscrowAccount? EscrowAccount { get; set; }
    public User? RaisedByUser { get; set; }

    public void Resolve(DisputeStatus resolution, string notes)
    {
        if (Status != DisputeStatus.PendingReview && Status != DisputeStatus.UnderArbitration)
            throw new InvalidStateTransitionException(nameof(Dispute), Status.ToString(), resolution.ToString());

        Status = resolution;
        ResolutionNotes = notes;
        ResolvedAt = DateTimeOffset.UtcNow;
    }
}
