using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Mappings;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
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
    private readonly ICurrentActor _actor;

    public GetBookingByIdQueryHandler(IReadDbContext readContext, ICurrentActor actor)
    {
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<BookingDto> Handle(GetBookingByIdQuery request, CancellationToken cancellationToken)
    {
        var dto = await _readContext.GetBookingDtoAsync(request.BookingId, cancellationToken);
        _actor.EnsureCanView(dto.Id, dto.BuyerId, dto.ProviderId);
        return dto;
    }
}

/// <summary>
/// Buyers see their own bookings, providers see bookings on their resources,
/// admins see every booking in the tenant. Tenant scoping comes from the global query filter.
/// </summary>
public record GetBookingsQuery(
    BookingStatus? Status = null,
    int Page = 1,
    int PageSize = 50
) : IRequest<IReadOnlyList<BookingDto>>;

public class GetBookingsQueryHandler : IRequestHandler<GetBookingsQuery, IReadOnlyList<BookingDto>>
{
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public GetBookingsQueryHandler(IReadDbContext readContext, ICurrentActor actor)
    {
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<IReadOnlyList<BookingDto>> Handle(GetBookingsQuery request, CancellationToken cancellationToken)
    {
        var query = _readContext.Bookings.AsQueryable();

        if (!_actor.IsAdmin())
        {
            var userId = _actor.RequireUserId();
            query = _actor.Role == UserRole.Provider
                ? query.Where(b => b.ProviderId == userId)
                : query.Where(b => b.BuyerId == userId);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(b => b.Status == request.Status.Value);
        }

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        return await query
            .OrderByDescending(b => b.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(BookingMappings.ToDto)
            .ToListAsync(cancellationToken);
    }
}

public record GetBookingWorkflowQuery(Guid BookingId) : IRequest<WorkflowStateDto>;

public class GetBookingWorkflowQueryHandler : IRequestHandler<GetBookingWorkflowQuery, WorkflowStateDto>
{
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;
    private readonly IEscrowWorkflowReader _workflowReader;

    public GetBookingWorkflowQueryHandler(IReadDbContext readContext, ICurrentActor actor, IEscrowWorkflowReader workflowReader)
    {
        _readContext = readContext;
        _actor = actor;
        _workflowReader = workflowReader;
    }

    public async Task<WorkflowStateDto> Handle(GetBookingWorkflowQuery request, CancellationToken cancellationToken)
    {
        var participants = await _readContext.Bookings
            .Where(b => b.Id == request.BookingId)
            .Select(b => new { b.BuyerId, b.ProviderId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        _actor.EnsureCanView(request.BookingId, participants.BuyerId, participants.ProviderId);

        // The saga row is created asynchronously when the outbox delivers BookingSlotReservedEvent,
        // so it can lag the booking by a moment.
        return await _workflowReader.GetAsync(request.BookingId, cancellationToken)
            ?? throw new NotFoundException("Escrow workflow", request.BookingId);
    }
}

public record GetAvailableSlotsQuery(
    Guid ResourceId,
    DateOnly TargetDate,
    int SlotDurationMinutes = 60
) : IRequest<IReadOnlyList<TimeSlotDto>>;

public class GetAvailableSlotsQueryHandler : IRequestHandler<GetAvailableSlotsQuery, IReadOnlyList<TimeSlotDto>>
{
    private static readonly BookingStatus[] FreeSlotStatuses =
        { BookingStatus.Cancelled, BookingStatus.Expired, BookingStatus.Refunded };

    private readonly IReadDbContext _readContext;

    public GetAvailableSlotsQueryHandler(IReadDbContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<IReadOnlyList<TimeSlotDto>> Handle(GetAvailableSlotsQuery request, CancellationToken cancellationToken)
    {
        var resourceExists = await _readContext.BookableResources
            .AnyAsync(r => r.Id == request.ResourceId, cancellationToken);
        if (!resourceExists)
            throw new NotFoundException(nameof(BookableResource), request.ResourceId);

        var slotMinutes = Math.Clamp(request.SlotDurationMinutes, 15, 240);

        // Operating window 08:00–20:00 UTC. Per-resource opening hours would be the next step.
        var dayStart = new DateTimeOffset(request.TargetDate.ToDateTime(new TimeOnly(8, 0)), TimeSpan.Zero);
        var dayEnd = new DateTimeOffset(request.TargetDate.ToDateTime(new TimeOnly(20, 0)), TimeSpan.Zero);

        var existing = await _readContext.Bookings
            .Where(b => b.ResourceId == request.ResourceId
                     && !FreeSlotStatuses.Contains(b.Status)
                     && b.Period.Start < dayEnd
                     && b.Period.End > dayStart)
            .Select(b => new { b.Period.Start, b.Period.End })
            .ToListAsync(cancellationToken);

        var slots = new List<TimeSlotDto>();
        var step = TimeSpan.FromMinutes(slotMinutes);
        for (var current = dayStart; current + step <= dayEnd; current += step)
        {
            var slotEnd = current + step;
            var taken = existing.Any(b => b.Start < slotEnd && b.End > current);
            slots.Add(new TimeSlotDto(current, slotEnd, !taken));
        }

        return slots;
    }
}
