using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Mappings;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Application.Common.Settings;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EscrowMarketplace.Application.Features.Bookings.Commands;

/// <summary>Reserve a slot. The buyer is always the authenticated user.</summary>
public record CreateBookingCommand(
    Guid ResourceId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime
) : IRequest<BookingDto>;

public class CreateBookingCommandValidator : AbstractValidator<CreateBookingCommand>
{
    public CreateBookingCommandValidator()
    {
        RuleFor(x => x.ResourceId).NotEmpty().WithMessage("Resource ID is required.");

        // Must() evaluates "now" on every validation, not once when the validator is built.
        RuleFor(x => x.StartTime)
            .Must(start => start > DateTimeOffset.UtcNow.AddMinutes(-5))
            .WithMessage("Booking start time must be in the present or future.");

        RuleFor(x => x.EndTime)
            .GreaterThan(x => x.StartTime)
            .WithMessage("Booking end time must be after the start time.");

        RuleFor(x => x)
            .Must(x => (x.EndTime - x.StartTime).TotalMinutes >= 15)
            .WithMessage("Booking duration must be at least 15 minutes.")
            .Must(x => (x.EndTime - x.StartTime).TotalHours <= 24)
            .WithMessage("Booking duration cannot exceed 24 hours.");
    }
}

public class CreateBookingCommandHandler : IRequestHandler<CreateBookingCommand, BookingDto>
{
    private static readonly BookingStatus[] FreeSlotStatuses =
        { BookingStatus.Cancelled, BookingStatus.Expired, BookingStatus.Refunded };

    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentTenantService _tenant;
    private readonly ICurrentActor _actor;
    private readonly EscrowWorkflowOptions _options;

    public CreateBookingCommandHandler(
        IMarketplaceDbContext context,
        IReadDbContext readContext,
        ICurrentTenantService tenant,
        ICurrentActor actor,
        IOptions<EscrowWorkflowOptions> options)
    {
        _context = context;
        _readContext = readContext;
        _tenant = tenant;
        _actor = actor;
        _options = options.Value;
    }

    public async Task<BookingDto> Handle(CreateBookingCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenant.RequireTenantId();
        var buyerId = _actor.RequireRole(UserRole.Buyer);

        var resource = await _context.BookableResources
            .FirstOrDefaultAsync(r => r.Id == request.ResourceId && r.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(BookableResource), request.ResourceId);

        var buyerExists = await _context.Users.AnyAsync(u => u.Id == buyerId && u.IsActive, cancellationToken);
        if (!buyerExists)
            throw new NotFoundException(nameof(User), buyerId);

        var period = new TimeRange(request.StartTime, request.EndTime);

        // Fast-fail for the common case with a friendly message. This check alone is NOT safe
        // under concurrency (two requests can both pass it); the PostgreSQL GiST exclusion
        // constraint is what actually guarantees no overlap, and its violation maps to 409 too.
        var hasConflict = await _context.Bookings
            .AnyAsync(b => b.ResourceId == request.ResourceId
                        && !FreeSlotStatuses.Contains(b.Status)
                        && b.Period.Start < period.End
                        && b.Period.End > period.Start, cancellationToken);

        if (hasConflict)
        {
            throw new DoubleBookingExclusionException(
                $"'{resource.Title}' is already booked for part of {period.Start:u} – {period.End:u}.");
        }

        var tenantRecord = await _context.Tenants.FirstAsync(t => t.Id == tenantId, cancellationToken);

        var booking = Booking.Reserve(
            tenantId, resource, buyerId, period,
            paymentDueUtc: DateTimeOffset.UtcNow.Add(_options.PaymentWindow));

        var escrow = EscrowAccount.Create(
            tenantId, booking.Id, buyerId, resource.ProviderId,
            booking.TotalAmount, tenantRecord.DefaultPlatformCommissionPercent, resource.Currency);

        booking.EscrowAccount = escrow;
        _context.Bookings.Add(booking);
        _context.EscrowAccounts.Add(escrow);

        // Booking, escrow and the BookingSlotReservedEvent outbox row commit in one transaction.
        await _context.SaveChangesAsync(cancellationToken);

        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}
