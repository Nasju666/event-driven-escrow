using EscrowMarketplace.Domain.Common;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Domain.Entities;

public class Dispute : BaseEntity, ITenantEntity, IAuditableEntity
{
    public Guid TenantId { get; set; }
    public Guid EscrowAccountId { get; set; }
    public Guid BookingId { get; set; }
    public Guid RaisedByUserId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string EvidenceSummary { get; set; } = string.Empty;
    public DisputeStatus Status { get; set; } = DisputeStatus.PendingReview;
    public string? ResolutionNotes { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }

    // Navigations
    public EscrowAccount? EscrowAccount { get; set; }
    public User? RaisedByUser { get; set; }

    public bool IsOpen => Status is DisputeStatus.PendingReview or DisputeStatus.UnderArbitration;

    public static Dispute Open(EscrowAccount escrow, Guid raisedByUserId, string reason, string evidenceSummary)
    {
        var dispute = new Dispute
        {
            Id = Guid.NewGuid(),
            TenantId = escrow.TenantId,
            EscrowAccountId = escrow.Id,
            BookingId = escrow.BookingId,
            RaisedByUserId = raisedByUserId,
            Reason = reason,
            EvidenceSummary = evidenceSummary,
            Status = DisputeStatus.PendingReview
        };

        dispute.AddDomainEvent(new EscrowDisputeRaisedEvent(
            escrow.BookingId, escrow.TenantId, dispute.Id, escrow.Id, raisedByUserId, reason));

        return dispute;
    }

    public void Resolve(DisputeStatus resolution, string notes)
    {
        if (resolution is not (DisputeStatus.ResolvedInFavorOfBuyer or DisputeStatus.ResolvedInFavorOfProvider))
            throw new DomainException("A dispute must be resolved in favor of either the buyer or the provider.");

        if (!IsOpen)
            throw new InvalidStateTransitionException(nameof(Dispute), Status.ToString(), resolution.ToString());

        Status = resolution;
        ResolutionNotes = notes;
        ResolvedAt = DateTimeOffset.UtcNow;

        AddDomainEvent(new EscrowDisputeResolvedEvent(BookingId, TenantId, Id, EscrowAccountId, resolution.ToString()));
    }
}
