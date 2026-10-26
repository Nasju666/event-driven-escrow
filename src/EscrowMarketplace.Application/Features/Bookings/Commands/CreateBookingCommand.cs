using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.ValueObjects;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Bookings.Commands;

public record CreateBookingCommand(
    Guid ResourceId,
    Guid BuyerId,
    DateTimeOffset StartTime,
    DateTimeOffset EndTime
) : IRequest<BookingDto>;

public class CreateBookingCommandValidator : AbstractValidator<CreateBookingCommand>
{
    public CreateBookingCommandValidator()
    {
        RuleFor(x => x.ResourceId).NotEmpty().WithMessage("Resource ID is required.");
        RuleFor(x => x.BuyerId).NotEmpty().WithMessage("Buyer ID is required.");
        RuleFor(x => x.StartTime)
            .GreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5))
            .WithMessage("Booking start time must be in the present or future.");
        RuleFor(x => x.EndTime)
            .GreaterThan(x => x.StartTime)
            .WithMessage("Booking end time must be after the start time.");
        RuleFor(x => x)
            .Must(x => (x.EndTime - x.StartTime).TotalMinutes >= 15)
            .WithMessage("Booking duration must be at least 15 minutes.");
    }
}

public class CreateBookingCommandHandler : IRequestHandler<CreateBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public CreateBookingCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<BookingDto> Handle(CreateBookingCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context is required to create a booking.");

        var resource = await _context.BookableResources
            .Include(r => r.Provider)
            .FirstOrDefaultAsync(r => r.Id == request.ResourceId && r.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(BookableResource), request.ResourceId);

        var buyer = await _context.Users
            .FirstOrDefaultAsync(u => u.Id == request.BuyerId && u.IsActive, cancellationToken)
            ?? throw new NotFoundException(nameof(User), request.BuyerId);

        var period = new TimeRange(request.StartTime, request.EndTime);

        // Pre-check for conflicting active bookings (Application-level fast-fail)
        var hasConflict = await _context.Bookings
            .AnyAsync(b => b.ResourceId == request.ResourceId
                        && b.Status != BookingStatus.Cancelled
                        && b.Status != BookingStatus.Expired
                        && b.Status != BookingStatus.Refunded
                        && b.Period.Start < request.EndTime
                        && b.Period.End > request.StartTime, cancellationToken);

        if (hasConflict)
        {
            throw new DoubleBookingExclusionException(
                $"The requested time slot [{request.StartTime:O} - {request.EndTime:O}] for resource '{resource.Title}' is already reserved.");
        }

        // Calculate pricing
        var durationHours = (decimal)period.Duration.TotalHours;
        var totalAmount = Math.Round(durationHours * resource.HourlyRate, 2);
        if (totalAmount <= 0) totalAmount = resource.HourlyRate;

        var tenant = await _context.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        var commissionPercent = tenant?.DefaultPlatformCommissionPercent ?? 10.0m;

        var bookingId = Guid.NewGuid();

        // Create Booking
        var booking = new Booking
        {
            Id = bookingId,
            TenantId = tenantId,
            ResourceId = resource.Id,
            BuyerId = buyer.Id,
            ProviderId = resource.ProviderId,
            Period = period,
            TotalAmount = totalAmount,
            Currency = resource.Currency,
            Status = BookingStatus.Held
        };

        // Create Escrow Account for this booking
        var escrow = EscrowAccount.Create(
            tenantId,
            bookingId,
            buyer.Id,
            resource.ProviderId,
            totalAmount,
            commissionPercent,
            resource.Currency);

        booking.EscrowAccount = escrow;

        // Stage Domain Event (Transactional Outbox)
        booking.AddDomainEvent(new BookingSlotReservedEvent(
            booking.Id,
            tenantId,
            resource.Id,
            buyer.Id,
            resource.ProviderId,
            period.Start,
            period.End,
            totalAmount,
            resource.Currency));

        _context.Bookings.Add(booking);
        _context.EscrowAccounts.Add(escrow);

        // Save atomically inside PostgreSQL transaction (Protected by Exclusion Constraint)
        await _context.SaveChangesAsync(cancellationToken);

        var dto = new BookingDto(
            booking.Id,
            tenantId,
            resource.Id,
            resource.Title,
            buyer.Id,
            buyer.FullName,
            resource.ProviderId,
            resource.Provider?.FullName ?? "Provider",
            period.Start,
            period.End,
            totalAmount,
            resource.Currency,
            booking.Status,
            booking.RowVersion,
            booking.CreatedAt);

        // SignalR Real-Time Notification
        await _notifier.NotifyBookingCreatedAsync(tenantId, booking.Id, dto, cancellationToken);

        return dto;
    }
}
