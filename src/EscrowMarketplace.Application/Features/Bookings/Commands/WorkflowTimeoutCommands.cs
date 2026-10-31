using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Security;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Application.Features.Bookings.Commands;

// Commands issued by the escrow saga when a deadline passes. They run as the system actor and
// are deliberately idempotent: if a user already moved the booking on (paid just in time,
// provider accepted, buyer disputed), the timeout is a no-op instead of an error.

public record ExpireUnpaidBookingCommand(Guid BookingId) : IRequest<bool>;

public class ExpireUnpaidBookingCommandHandler : IRequestHandler<ExpireUnpaidBookingCommand, bool>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ExpireUnpaidBookingCommandHandler> _logger;

    public ExpireUnpaidBookingCommandHandler(
        IMarketplaceDbContext context, ICurrentActor actor, ILogger<ExpireUnpaidBookingCommandHandler> logger)
    {
        _context = context;
        _actor = actor;
        _logger = logger;
    }

    public async Task<bool> Handle(ExpireUnpaidBookingCommand request, CancellationToken cancellationToken)
    {
        _actor.RequireSystem();
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);

        if (booking.Status != BookingStatus.PendingPayment)
        {
            _logger.LogInformation("Payment timeout for {BookingId} ignored: booking is already {Status}.", booking.Id, booking.Status);
            return false;
        }

        booking.Expire();
        booking.RequireEscrow().Void();

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public record CancelUnconfirmedBookingCommand(Guid BookingId) : IRequest<bool>;

public class CancelUnconfirmedBookingCommandHandler : IRequestHandler<CancelUnconfirmedBookingCommand, bool>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentActor _actor;
    private readonly ILogger<CancelUnconfirmedBookingCommandHandler> _logger;

    public CancelUnconfirmedBookingCommandHandler(
        IMarketplaceDbContext context, ICurrentActor actor, ILogger<CancelUnconfirmedBookingCommandHandler> logger)
    {
        _context = context;
        _actor = actor;
        _logger = logger;
    }

    public async Task<bool> Handle(CancelUnconfirmedBookingCommand request, CancellationToken cancellationToken)
    {
        _actor.RequireSystem();
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);

        if (booking.Status != BookingStatus.Held)
        {
            _logger.LogInformation("Provider-response timeout for {BookingId} ignored: booking is already {Status}.", booking.Id, booking.Status);
            return false;
        }

        booking.CancelAndSettle("The provider did not respond in time. Full refund issued.");

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public record ReleaseAfterInspectionCommand(Guid BookingId) : IRequest<bool>;

public class ReleaseAfterInspectionCommandHandler : IRequestHandler<ReleaseAfterInspectionCommand, bool>
{
    private readonly IMarketplaceDbContext _context;
    private readonly ICurrentActor _actor;
    private readonly ILogger<ReleaseAfterInspectionCommandHandler> _logger;

    public ReleaseAfterInspectionCommandHandler(
        IMarketplaceDbContext context, ICurrentActor actor, ILogger<ReleaseAfterInspectionCommandHandler> logger)
    {
        _context = context;
        _actor = actor;
        _logger = logger;
    }

    public async Task<bool> Handle(ReleaseAfterInspectionCommand request, CancellationToken cancellationToken)
    {
        _actor.RequireSystem();
        var booking = await _context.LoadForUpdateAsync(request.BookingId, cancellationToken);

        if (booking.Status != BookingStatus.Completed)
        {
            _logger.LogInformation("Inspection timeout for {BookingId} ignored: booking is already {Status}.", booking.Id, booking.Status);
            return false;
        }

        EscrowSettlement.ReleaseToProvider(booking, booking.RequireEscrow(), booking.RequireProvider());

        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }
}
