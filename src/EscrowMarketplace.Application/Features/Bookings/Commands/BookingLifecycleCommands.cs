using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Mappings;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Application.Common.Settings;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.Services;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EscrowMarketplace.Application.Features.Bookings.Commands;

internal static class BookingCommandSupport
{
    /// <summary>Loads the booking aggregate with everything a lifecycle transition may touch.</summary>
    public static async Task<Booking> LoadForUpdateAsync(
        this IMarketplaceDbContext context, Guid bookingId, CancellationToken cancellationToken)
    {
        return await context.Bookings
            .Include(b => b.EscrowAccount)
            .Include(b => b.Buyer)
            .Include(b => b.Provider)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException(nameof(Booking), bookingId);
    }

    public static EscrowAccount RequireEscrow(this Booking booking) =>
        booking.EscrowAccount ?? throw new DomainException($"Booking {booking.Id} has no escrow account.");

    public static User RequireBuyer(this Booking booking) =>
        booking.Buyer ?? throw new DomainException($"Buyer for booking {booking.Id} could not be loaded.");

    public static User RequireProvider(this Booking booking) =>
        booking.Provider ?? throw new DomainException($"Provider for booking {booking.Id} could not be loaded.");

    /// <summary>
    /// Cancels the booking and settles the escrow: a full refund if money was captured,
    /// otherwise the escrow is simply voided. Policy: full refund for any cancellation before
    /// completion. A real marketplace would apply a cancellation policy here.
    /// </summary>
    public static void CancelAndSettle(this Booking booking, string reason)
    {
        booking.Cancel(reason);

        var escrow = booking.RequireEscrow();
        if (escrow.Status == EscrowStatus.Funded)
        {
            EscrowSettlement.RefundToBuyer(booking, escrow, booking.RequireBuyer(), reason);
        }
        else if (escrow.Status is EscrowStatus.PendingAuthorization or EscrowStatus.Authorized)
        {
            escrow.Void();
        }
    }
}

// ───────────────────────────── Pay (buyer) ─────────────────────────────

public record PayForBookingCommand(Guid BookingId) : IRequest<BookingDto>;

public class PayForBookingCommandHandler : IRequestHandler<PayForBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;
    private readonly EscrowWorkflowOptions _options;

    public PayForBookingCommandHandler(
        IMarketplaceDbContext context, IReadDbContext readContext, ICurrentActor actor,
        IOptions<EscrowWorkflowOptions> options)
    {
        _context = context;
        _readContext = readContext;
        _actor = actor;
        _options = options.Value;
    }

    public async Task<BookingDto> Handle(PayForBookingCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureIsBuyerOf(booking);

        if (DateTimeOffset.UtcNow > booking.PaymentDueUtc)
            throw new DomainException("The payment window for this booking has closed.");

        var escrow = booking.RequireEscrow();

        // Simulated payment: funds move from the buyer's wallet into the escrow hold.
        // Authorize + capture mirror how a real PSP (Stripe, PayMongo) would be integrated here.
        booking.RequireBuyer().Debit(escrow.TotalAmount);
        escrow.Authorize($"wallet_{Guid.NewGuid():N}");
        escrow.Capture();
        booking.MarkPaid(DateTimeOffset.UtcNow.Add(_options.ProviderResponseWindow));

        await _context.SaveChangesAsync(cancellationToken);
        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}

// ─────────────────────────── Confirm (provider) ───────────────────────────

public record ConfirmBookingCommand(Guid BookingId) : IRequest<BookingDto>;

public class ConfirmBookingCommandHandler : IRequestHandler<ConfirmBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public ConfirmBookingCommandHandler(IMarketplaceDbContext context, IReadDbContext readContext, ICurrentActor actor)
    {
        _context = context;
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<BookingDto> Handle(ConfirmBookingCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureIsProviderOf(booking);

        booking.Confirm();

        await _context.SaveChangesAsync(cancellationToken);
        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}

// ─────────────────────────── Complete (provider) ───────────────────────────

public record CompleteBookingCommand(Guid BookingId) : IRequest<BookingDto>;

public class CompleteBookingCommandHandler : IRequestHandler<CompleteBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;
    private readonly EscrowWorkflowOptions _options;

    public CompleteBookingCommandHandler(
        IMarketplaceDbContext context, IReadDbContext readContext, ICurrentActor actor,
        IOptions<EscrowWorkflowOptions> options)
    {
        _context = context;
        _readContext = readContext;
        _actor = actor;
        _options = options.Value;
    }

    public async Task<BookingDto> Handle(CompleteBookingCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureIsProviderOf(booking);

        // Completing starts the inspection window. If the buyer neither approves nor disputes
        // before the deadline, the saga's timeout releases the payout automatically.
        var deadline = DateTimeOffset.UtcNow.Add(_options.InspectionWindow);
        booking.RequireEscrow().StartInspectionWindow(deadline);
        booking.Complete(deadline);

        await _context.SaveChangesAsync(cancellationToken);
        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}

// ───────────────────── Approve & release early (buyer) ─────────────────────

public record ApproveBookingCommand(Guid BookingId) : IRequest<BookingDto>;

public class ApproveBookingCommandHandler : IRequestHandler<ApproveBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public ApproveBookingCommandHandler(IMarketplaceDbContext context, IReadDbContext readContext, ICurrentActor actor)
    {
        _context = context;
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<BookingDto> Handle(ApproveBookingCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureIsBuyerOf(booking);

        if (booking.Status != BookingStatus.Completed)
            throw new DomainException("You can only approve a booking after the provider has marked it complete.");

        EscrowSettlement.ReleaseToProvider(booking, booking.RequireEscrow(), booking.RequireProvider());

        await _context.SaveChangesAsync(cancellationToken);
        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}

// ───────────────────── Cancel (buyer, provider or admin) ─────────────────────

public record CancelBookingCommand(Guid BookingId, string Reason) : IRequest<BookingDto>;

public class CancelBookingCommandValidator : AbstractValidator<CancelBookingCommand>
{
    public CancelBookingCommandValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public class CancelBookingCommandHandler : IRequestHandler<CancelBookingCommand, BookingDto>
{
    private readonly IMarketplaceDbContext _context;
    private readonly IReadDbContext _readContext;
    private readonly ICurrentActor _actor;

    public CancelBookingCommandHandler(IMarketplaceDbContext context, IReadDbContext readContext, ICurrentActor actor)
    {
        _context = context;
        _readContext = readContext;
        _actor = actor;
    }

    public async Task<BookingDto> Handle(CancelBookingCommand request, CancellationToken cancellationToken)
    {
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);
        _actor.EnsureCanView(booking); // participants and admins only

        booking.CancelAndSettle(request.Reason);

        await _context.SaveChangesAsync(cancellationToken);
        return await _readContext.GetBookingDtoAsync(booking.Id, cancellationToken);
    }
}
