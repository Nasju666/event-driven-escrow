using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Escrows.Queries;

public record GetEscrowByBookingIdQuery(Guid BookingId) : IRequest<EscrowDto>;

public class GetEscrowByBookingIdQueryHandler : IRequestHandler<GetEscrowByBookingIdQuery, EscrowDto>
{
    private readonly IReadDbContext _readContext;

    public GetEscrowByBookingIdQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<EscrowDto> Handle(GetEscrowByBookingIdQuery request, CancellationToken cancellationToken)
    {
        var escrow = await _readContext.EscrowAccounts
            .AsNoTracking()
            .Include(e => e.LedgerEntries)
            .FirstOrDefaultAsync(e => e.BookingId == request.BookingId, cancellationToken)
            ?? throw new NotFoundException("Escrow for Booking", request.BookingId);

        return new EscrowDto(
            escrow.Id,
            escrow.BookingId,
            escrow.TenantId,
            escrow.BuyerId,
            escrow.ProviderId,
            escrow.TotalAmount,
            escrow.PlatformCommissionPercent,
            escrow.PlatformCommissionAmount,
            escrow.ProviderPayoutAmount,
            escrow.Currency,
            escrow.Status,
            escrow.PaymentReference,
            escrow.AuthorizedAt,
            escrow.FundedAt,
            escrow.SettledAt,
            escrow.RefundedAt,
            escrow.InspectionDeadlineUtc,
            escrow.RowVersion,
            escrow.CreatedAt,
            escrow.LedgerEntries.Select(l => new EscrowLedgerEntryDto(
                l.Id, l.EntryType, l.Amount, l.Currency, l.Description, l.CreatedAt)).ToList()
        );
    }
}

public record GetDisputesQuery : IRequest<IReadOnlyList<DisputeDto>>;

public class GetDisputesQueryHandler : IRequestHandler<GetDisputesQuery, IReadOnlyList<DisputeDto>>
{
    private readonly IReadDbContext _readContext;

    public GetDisputesQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<IReadOnlyList<DisputeDto>> Handle(GetDisputesQuery request, CancellationToken cancellationToken)
    {
        return await _readContext.Disputes
            .AsNoTracking()
            .Include(d => d.RaisedByUser)
            .Include(d => d.EscrowAccount)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DisputeDto(
                d.Id,
                d.EscrowAccountId,
                d.EscrowAccount != null ? d.EscrowAccount.BookingId : Guid.Empty,
                d.RaisedByUserId,
                d.RaisedByUser != null ? d.RaisedByUser.FullName : string.Empty,
                d.Reason,
                d.EvidenceSummary,
                d.Status,
                d.ResolutionNotes,
                d.ResolvedAt,
                d.CreatedAt
            ))
            .ToListAsync(cancellationToken);
    }
}
