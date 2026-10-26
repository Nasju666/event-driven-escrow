using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Application.Features.Bookings.Commands;

public record ConfirmBookingCommand(Guid BookingId, Guid ProviderId) : IRequest<BookingDto>;

public class ConfirmBookingCommandValidator : AbstractValidator<ConfirmBookingCommand>
{
    public ConfirmBookingCommandValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.ProviderId).NotEmpty();
    }
}

public class ConfirmBookingCommandHandler : IRequestHandler<ConfirmBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public ConfirmBookingCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<BookingDto> Handle(ConfirmBookingCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context is required.");

        var booking = await _context.Bookings
            .Include(b => b.Resource)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .Include(b => b.EscrowAccount)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        if (booking.ProviderId != request.ProviderId)
        {
            throw new DomainException("Only the designated service provider can confirm this booking.");
        }

        booking.Confirm();
        booking.AddDomainEvent(new ProviderConfirmedBookingEvent(booking.Id, tenantId, booking.ProviderId));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStatusChangedAsync(
            tenantId, booking.Id, booking.Status.ToString(), "Provider confirmed reservation.", cancellationToken);

        return new BookingDto(
            booking.Id,
            tenantId,
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

public record CompleteBookingCommand(Guid BookingId) : IRequest<BookingDto>;

public class CompleteBookingCommandHandler : IRequestHandler<CompleteBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public CompleteBookingCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<BookingDto> Handle(CompleteBookingCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context is required.");

        var booking = await _context.Bookings
            .Include(b => b.Resource)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .Include(b => b.EscrowAccount)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        booking.Complete();
        booking.AddDomainEvent(new ServiceCompletedEvent(booking.Id, tenantId));

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStatusChangedAsync(
            tenantId, booking.Id, booking.Status.ToString(), "Service completed successfully.", cancellationToken);

        return new BookingDto(
            booking.Id,
            tenantId,
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

public record CancelBookingCommand(Guid BookingId, string Reason) : IRequest<BookingDto>;

public class CancelBookingCommandHandler : IRequestHandler<CancelBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentTenantService _tenantService;
    private readonly IBookingNotifier _notifier;

    public CancelBookingCommandHandler(
        IMarketplaceDbContext context,
        ICurrentTenantService tenantService,
        IBookingNotifier notifier)
    {
        _context = context;
        _tenantService = tenantService;
        _notifier = notifier;
    }

    public async Task<BookingDto> Handle(CancelBookingCommand request, CancellationToken cancellationToken)
    {
        var tenantId = _tenantService.TenantId 
            ?? throw new DomainException("Tenant context is required.");

        var booking = await _context.Bookings
            .Include(b => b.Resource)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .Include(b => b.EscrowAccount)
            .FirstOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        booking.Cancel();
        booking.AddDomainEvent(new BookingCancelledEvent(booking.Id, tenantId, booking.ResourceId, request.Reason));

        // If escrow was funded or authorized, initiate refund
        if (booking.EscrowAccount != null && booking.EscrowAccount.Status == EscrowStatus.Funded)
        {
            booking.EscrowAccount.Refund($"Booking cancelled: {request.Reason}");
            booking.Refund();
        }

        await _context.SaveChangesAsync(cancellationToken);

        await _notifier.NotifyStatusChangedAsync(
            tenantId, booking.Id, booking.Status.ToString(), $"Booking cancelled: {request.Reason}", cancellationToken);

        return new BookingDto(
            booking.Id,
            tenantId,
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
