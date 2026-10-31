using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Mappings;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Escrows.Queries;

public record GetEscrowByBookingIdQuery(Guid BookingId) : IRequest<EscrowDto>;

public class GetEscrowByBookingIdQueryHandler : IRequestHandler<GetEscrowByBookingIdQuery, EscrowDto>
{
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public GetEscrowByBookingIdQueryHandler(IReadDbContext readContext, ICurrentActor actor)
    {
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<EscrowDto> Handle(GetEscrowByBookingIdQuery request, CancellationToken cancellationToken)
    {
        var escrow = await _readContext.EscrowAccounts
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.BookingId == request.BookingId, cancellationToken)
            ?? throw new NotFoundException("Escrow for Booking", request.BookingId);

        _actor.EnsureCanView(request.BookingId, escrow.BuyerId, escrow.ProviderId);
        return escrow.ToEscrowDto();
    }
}

/// <summary>Admins see every dispute in the tenant; buyers and providers see disputes on their own bookings.</summary>
public record GetDisputesQuery(bool OpenOnly = false) : IRequest<IReadOnlyList<DisputeDto>>;

public class GetDisputesQueryHandler : IRequestHandler<GetDisputesQuery, IReadOnlyList<DisputeDto>>
{
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public GetDisputesQueryHandler(IReadDbContext readContext, ICurrentActor actor)
    {
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<IReadOnlyList<DisputeDto>> Handle(GetDisputesQuery request, CancellationToken cancellationToken)
    {
        var query = _readContext.Disputes.AsQueryable();

        if (!_actor.IsAdmin())
        {
            var userId = _actor.RequireUserId();
            query = query.Where(d => d.EscrowAccount != null
                && (d.EscrowAccount.BuyerId == userId || d.EscrowAccount.ProviderId == userId));
        }

        if (request.OpenOnly)
        {
            query = query.Where(d => d.Status == DisputeStatus.PendingReview || d.Status == DisputeStatus.UnderArbitration);
        }

        return await query
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DisputeDto(
                d.Id,
                d.EscrowAccountId,
                d.BookingId,
                d.RaisedByUserId,
                d.RaisedByUser != null ? d.RaisedByUser.FullName : string.Empty,
                d.Reason,
                d.EvidenceSummary,
                d.Status,
                d.ResolutionNotes,
                d.ResolvedAt,
                d.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}
