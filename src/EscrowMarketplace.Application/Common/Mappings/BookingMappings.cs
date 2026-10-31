using System.Linq.Expressions;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Common.Mappings;

public static class BookingMappings
{
    /// <summary>Single SQL projection used by every query and command that returns a booking.</summary>
    public static readonly Expression<Func<Booking, BookingDto>> ToDto = b => new BookingDto(
        b.Id,
        b.TenantId,
        b.ResourceId,
        b.Resource != null ? b.Resource.Title : string.Empty,
        b.BuyerId,
        b.Buyer != null ? b.Buyer.FullName : string.Empty,
        b.ProviderId,
        b.Provider != null ? b.Provider.FullName : string.Empty,
        b.Period.Start,
        b.Period.End,
        b.TotalAmount,
        b.Currency,
        b.Status,
        b.PaymentDueUtc,
        b.ProviderResponseDueUtc,
        b.CancellationReason,
        b.CreatedAt,
        b.EscrowAccount != null ? b.EscrowAccount.Id : (Guid?)null,
        b.EscrowAccount != null ? b.EscrowAccount.Status : (EscrowStatus?)null,
        b.EscrowAccount != null ? b.EscrowAccount.PlatformCommissionPercent : 0m,
        b.EscrowAccount != null ? b.EscrowAccount.PlatformCommissionAmount : 0m,
        b.EscrowAccount != null ? b.EscrowAccount.ProviderPayoutAmount : 0m,
        b.EscrowAccount != null ? b.EscrowAccount.InspectionDeadlineUtc : null,
        b.EscrowAccount != null
            ? b.EscrowAccount.Disputes
                .Where(d => d.Status == DisputeStatus.PendingReview || d.Status == DisputeStatus.UnderArbitration)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefault()
            : null);

    public static async Task<BookingDto> GetBookingDtoAsync(
        this IReadDbContext readContext, Guid bookingId, CancellationToken cancellationToken)
    {
        return await readContext.Bookings
            .Where(b => b.Id == bookingId)
            .Select(ToDto)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), bookingId);
    }

    public static EscrowDto ToEscrowDto(this EscrowAccount e) => new(
        e.Id,
        e.BookingId,
        e.TenantId,
        e.BuyerId,
        e.ProviderId,
        e.TotalAmount,
        e.PlatformCommissionPercent,
        e.PlatformCommissionAmount,
        e.ProviderPayoutAmount,
        e.Currency,
        e.Status,
        e.PaymentReference,
        e.AuthorizedAt,
        e.FundedAt,
        e.SettledAt,
        e.RefundedAt,
        e.InspectionDeadlineUtc,
        e.CreatedAt,
        e.LedgerEntries
            .OrderBy(l => l.CreatedAt)
            .Select(l => new EscrowLedgerEntryDto(l.Id, l.EntryType, l.Amount, l.Currency, l.Description, l.CreatedAt))
            .ToList());

    public static DisputeDto ToDisputeDto(this Dispute d, string raisedByName) => new(
        d.Id,
        d.EscrowAccountId,
        d.BookingId,
        d.RaisedByUserId,
        raisedByName,
        d.Reason,
        d.EvidenceSummary,
        d.Status,
        d.ResolutionNotes,
        d.ResolvedAt,
        d.CreatedAt);
}
