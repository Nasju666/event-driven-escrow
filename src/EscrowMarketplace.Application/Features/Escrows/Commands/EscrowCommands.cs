using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Mappings;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.Services;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Escrows.Commands;

// ─────────────────────── Raise dispute (buyer or provider) ───────────────────────

public record RaiseDisputeCommand(Guid BookingId, string Reason, string EvidenceSummary) : IRequest<DisputeDto>;

public class RaiseDisputeCommandValidator : AbstractValidator<RaiseDisputeCommand>
{
    public RaiseDisputeCommandValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
        RuleFor(x => x.EvidenceSummary).MaximumLength(2000);
    }
}

public class RaiseDisputeCommandHandler : IRequestHandler<RaiseDisputeCommand, DisputeDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentActor _actor;

    public RaiseDisputeCommandHandler(IMarketplaceDbContext context, ICurrentActor actor)
    {
        _context = context;
        _actor = actor;
    }

    public async Task<DisputeDto> Handle(RaiseDisputeCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureCanView(booking);
        var userId = _actor.RequireUserId();

        if (!booking.IsParticipant(userId))
            throw new ForbiddenAccessException("Only the buyer or provider of this booking can raise a dispute.");

        var escrow = booking.RequireEscrow();

        // Freezes the money: neither the buyer's approval nor the inspection timeout can release
        // it while the dispute is open. Only an admin resolution moves it.
        booking.MarkDisputed();
        escrow.MarkDisputed();

        var dispute = Dispute.Open(escrow, userId, request.Reason, request.EvidenceSummary ?? string.Empty);
        _context.Disputes.Add(dispute);

        await _context.SaveChangesAsync(cancellationToken);

        var raisedBy = booking.BuyerId == userId ? booking.Buyer : booking.Provider;
        return dispute.ToDisputeDto(raisedBy?.FullName ?? string.Empty);
    }
}

// ─────────────────────────── Resolve dispute (admin) ───────────────────────────

public record ResolveDisputeCommand(Guid DisputeId, DisputeStatus Resolution, string ResolutionNotes) : IRequest<DisputeDto>;

public class ResolveDisputeCommandValidator : AbstractValidator<ResolveDisputeCommand>
{
    public ResolveDisputeCommandValidator()
    {
        RuleFor(x => x.DisputeId).NotEmpty();
        RuleFor(x => x.Resolution)
            .Must(r => r is DisputeStatus.ResolvedInFavorOfBuyer or DisputeStatus.ResolvedInFavorOfProvider)
            .WithMessage("Resolution must be ResolvedInFavorOfBuyer or ResolvedInFavorOfProvider.");
        RuleFor(x => x.ResolutionNotes).NotEmpty().MaximumLength(2000);
    }
}

public class ResolveDisputeCommandHandler : IRequestHandler<ResolveDisputeCommand, DisputeDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentActor _actor;

    public ResolveDisputeCommandHandler(IMarketplaceDbContext context, ICurrentActor actor)
    {
        _context = context;
        _actor = actor;
    }

    public async Task<DisputeDto> Handle(ResolveDisputeCommand request, CancellationToken cancellationToken)
    {
        _actor.RequireUserId();
        if (!_actor.IsAdmin())
            throw new ForbiddenAccessException("Only an admin can resolve disputes.");

        var dispute = await _context.Disputes
            .Include(d => d.RaisedByUser)
            .FirstOrDefaultAsync(d => d.Id == request.DisputeId, cancellationToken)
            ?? throw new NotFoundException(nameof(Dispute), request.DisputeId);

        var booking = await _context.LoadForUpdateAsync(dispute.BookingId, cancellationToken);
        var escrow = booking.RequireEscrow();

        dispute.Resolve(request.Resolution, request.ResolutionNotes);

        if (request.Resolution == DisputeStatus.ResolvedInFavorOfBuyer)
        {
            EscrowSettlement.RefundToBuyer(booking, escrow, booking.RequireBuyer(),
                $"Dispute resolved in favor of buyer: {request.ResolutionNotes}");
        }
        else
        {
            // Previously broken: ReleasePayout only accepted Funded, so this path always threw.
            EscrowSettlement.ReleaseToProvider(booking, escrow, booking.RequireProvider());
        }

        await _context.SaveChangesAsync(cancellationToken);
        return dispute.ToDisputeDto(dispute.RaisedByUser?.FullName ?? string.Empty);
    }
}
