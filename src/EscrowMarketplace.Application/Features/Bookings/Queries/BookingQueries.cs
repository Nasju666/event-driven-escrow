using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Bookings.Queries;

public record GetBookingByIdQuery(Guid BookingId) : IRequest<BookingDto>;

public class GetBookingByIdQueryHandler : IRequestHandler<GetBookingByIdQuery, BookingDto>
{
    private readonly IReadDbContext _readContext;

    public GetBookingByIdQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<BookingDto> Handle(GetBookingByIdQuery request, CancellationToken cancellationToken)
    {
        var booking = await _readContext.Bookings
            .AsNoTracking()
            .Include(b => b.Resource)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
            ?? throw new NotFoundException(nameof(BookingDto), request.BookingId);

        return new BookingDto(
            booking.Id,
            booking.TenantId,
            booking.ResourceId,
            booking.Resource?.Title ?? string.Empty,
            booking.BuyerId,
            booking.Buyer?.FullName ?? string.Empty,
            booking.ProviderId,
            booking.Provider?.FullName ?? string.Empty,
            booking.Period.Start,
            booking.Period.End,
            booking.TotalAmount,
            booking.Currency,
            booking.Status,
            booking.RowVersion,
            booking.CreatedAt);
    }
}

public record GetBookingsByTenantQuery(
    BookingStatus? Status = null,
    Guid? BuyerId = null,
    Guid? ProviderId = null,
    int Page = 1,
    int PageSize = 20
) : IRequest<IReadOnlyList<BookingDto>>;

public class GetBookingsByTenantQueryHandler : IRequestHandler<GetBookingsByTenantQuery, IReadOnlyList<BookingDto>>
{
    private readonly IReadDbContext _readContext;

    public GetBookingsByTenantQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<IReadOnlyList<BookingDto>> Handle(GetBookingsByTenantQuery request, CancellationToken cancellationToken)
    {
        var query = _readContext.Bookings
            .AsNoTracking()
            .Include(b => b.Resource)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .AsQueryable();

        if (request.Status.HasValue)
        {
            query = query.Where(b => b.Status == request.Status.Value);
        }

        if (request.BuyerId.HasValue)
        {
            query = query.Where(b => b.BuyerId == request.BuyerId.Value);
        }

        if (request.ProviderId.HasValue)
        {
            query = query.Where(b => b.ProviderId == request.ProviderId.Value);
        }

        var results = await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(b => new BookingDto(
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
                b.RowVersion,
                b.CreatedAt
            ))
            .ToListAsync(cancellationToken);

        return results;
    }
}

public record GetAvailableSlotsQuery(
    Guid ResourceId,
    DateOnly TargetDate,
    int SlotDurationMinutes = 60
) : IRequest<IReadOnlyList<TimeSlotDto>>;

public class GetAvailableSlotsQueryHandler : IRequestHandler<GetAvailableSlotsQuery, IReadOnlyList<TimeSlotDto>>
{
    private readonly IReadDbContext _readContext;

    public GetAvailableSlotsQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<IReadOnlyList<TimeSlotDto>> Handle(GetAvailableSlotsQuery request, CancellationToken cancellationToken)
    {
        var resource = await _readContext.BookableResources
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.ResourceId, cancellationToken)
            ?? throw new NotFoundException(nameof(BookableResource), request.ResourceId);

        // Define operating window (8:00 AM to 8:00 PM UTC by default)
        var dayStartUtc = request.TargetDate.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);
        var dayEndUtc = request.TargetDate.ToDateTime(new TimeOnly(20, 0), DateTimeKind.Utc);

        var existingBookings = await _readContext.Bookings
            .AsNoTracking()
            .Where(b => b.ResourceId == request.ResourceId
                     && b.Status != BookingStatus.Cancelled
                     && b.Status != BookingStatus.Expired
                     && b.Status != BookingStatus.Refunded
                     && b.Period.Start < dayEndUtc
                     && b.Period.End > dayStartUtc)
            .ToListAsync(cancellationToken);

        var slots = new List<TimeSlotDto>();
        var current = dayStartUtc;
        var step = TimeSpan.FromMinutes(request.SlotDurationMinutes);

        while (current + step <= dayEndUtc)
        {
            var slotEnd = current + step;
            var isOverlapping = existingBookings.Any(b => b.Period.Start < slotEnd && b.Period.End > current);

            slots.Add(new TimeSlotDto(
                StartTime: current,
                EndTime: slotEnd,
                IsAvailable: !isOverlapping
            ));

            current = slotEnd;
        }

        return slots;
    }
}
