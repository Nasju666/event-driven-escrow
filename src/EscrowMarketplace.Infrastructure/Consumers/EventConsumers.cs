using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Infrastructure.Persistence;
using EscrowMarketplace.Infrastructure.Saga;
using EscrowMarketplace.Infrastructure.Services;
using MassTransit;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Consumers;

/// <summary>
/// Runs the commands the escrow saga issues when a deadline passes. Each message runs in its own
/// DI scope, so it sets the tenant and a system actor for that scope before calling the normal
/// MediatR handler. The domain rules and tenant query filters still apply.
/// </summary>
public class WorkflowCommandConsumer :
    IConsumer<ExpireUnpaidBooking>,
    IConsumer<CancelUnconfirmedBooking>,
    IConsumer<ReleaseEscrowAfterInspection>
{
    private readonly ISender _sender;
    private readonly ICurrentTenantService _tenant;
    private readonly ICurrentActor _actor;
    private readonly ILogger<WorkflowCommandConsumer> _logger;

    public WorkflowCommandConsumer(
        ISender sender, ICurrentTenantService tenant, ICurrentActor actor, ILogger<WorkflowCommandConsumer> logger)
    {
        _sender = sender;
        _tenant = tenant;
        _actor = actor;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<ExpireUnpaidBooking> context) =>
        RunAsSystem(context.Message.TenantId, () => _sender.Send(new ExpireUnpaidBookingCommand(context.Message.BookingId), context.CancellationToken), "expire", context.Message.BookingId);

    public Task Consume(ConsumeContext<CancelUnconfirmedBooking> context) =>
        RunAsSystem(context.Message.TenantId, () => _sender.Send(new CancelUnconfirmedBookingCommand(context.Message.BookingId), context.CancellationToken), "cancel-unconfirmed", context.Message.BookingId);

    public Task Consume(ConsumeContext<ReleaseEscrowAfterInspection> context) =>
        RunAsSystem(context.Message.TenantId, () => _sender.Send(new ReleaseAfterInspectionCommand(context.Message.BookingId), context.CancellationToken), "auto-release", context.Message.BookingId);

    private async Task RunAsSystem(Guid tenantId, Func<Task<bool>> command, string action, Guid bookingId)
    {
        _tenant.SetTenant(tenantId, "workflow");
        _actor.SetSystem();

        var applied = await command();
        _logger.LogInformation("Workflow command {Action} for booking {BookingId}: {Outcome}",
            action, bookingId, applied ? "applied" : "skipped (booking already moved on)");
    }
}

/// <summary>
/// Pushes domain events and saga transitions to the browsers of the people involved
/// (buyer, provider and the tenant's admins) over SignalR. Clients then re-fetch through the API,
/// so authorization is enforced there and only ids and short messages go over the socket.
/// </summary>
public class RealtimeNotificationConsumer :
    IConsumer<BookingSlotReservedEvent>,
    IConsumer<BookingPaidEvent>,
    IConsumer<ProviderConfirmedBookingEvent>,
    IConsumer<ServiceCompletedEvent>,
    IConsumer<EscrowPayoutReleasedEvent>,
    IConsumer<EscrowRefundedEvent>,
    IConsumer<BookingCancelledEvent>,
    IConsumer<BookingExpiredEvent>,
    IConsumer<EscrowDisputeRaisedEvent>,
    IConsumer<EscrowDisputeResolvedEvent>,
    IConsumer<EscrowWorkflowTransitioned>
{
    private readonly MarketplaceDbContext _db;
    private readonly IBookingNotifier _notifier;
    private readonly ILogger<RealtimeNotificationConsumer> _logger;

    public RealtimeNotificationConsumer(MarketplaceDbContext db, IBookingNotifier notifier, ILogger<RealtimeNotificationConsumer> logger)
    {
        _db = db;
        _notifier = notifier;
        _logger = logger;
    }

    public Task Consume(ConsumeContext<BookingSlotReservedEvent> c) =>
        Push(c.Message, nameof(BookingSlotReservedEvent), $"Slot reserved ({c.Message.TotalAmount:0.00} {c.Message.Currency}). Awaiting payment.", c.CancellationToken);

    public Task Consume(ConsumeContext<BookingPaidEvent> c) =>
        Push(c.Message, nameof(BookingPaidEvent), "Buyer paid. Funds are held in escrow.", c.CancellationToken);

    public Task Consume(ConsumeContext<ProviderConfirmedBookingEvent> c) =>
        Push(c.Message, nameof(ProviderConfirmedBookingEvent), "Provider accepted the booking.", c.CancellationToken);

    public Task Consume(ConsumeContext<ServiceCompletedEvent> c) =>
        Push(c.Message, nameof(ServiceCompletedEvent), "Session marked complete. Inspection window started.", c.CancellationToken);

    public Task Consume(ConsumeContext<EscrowPayoutReleasedEvent> c) =>
        Push(c.Message, nameof(EscrowPayoutReleasedEvent), $"Payout of {c.Message.ProviderPayoutAmount:0.00} released to the provider.", c.CancellationToken);

    public Task Consume(ConsumeContext<EscrowRefundedEvent> c) =>
        Push(c.Message, nameof(EscrowRefundedEvent), $"Refund of {c.Message.Amount:0.00} returned to the buyer.", c.CancellationToken);

    public Task Consume(ConsumeContext<BookingCancelledEvent> c) =>
        Push(c.Message, nameof(BookingCancelledEvent), $"Booking cancelled: {c.Message.Reason}", c.CancellationToken);

    public Task Consume(ConsumeContext<BookingExpiredEvent> c) =>
        Push(c.Message, nameof(BookingExpiredEvent), "Payment window elapsed. Slot released.", c.CancellationToken);

    public Task Consume(ConsumeContext<EscrowDisputeRaisedEvent> c) =>
        Push(c.Message, nameof(EscrowDisputeRaisedEvent), $"Dispute raised: {c.Message.Reason}", c.CancellationToken);

    public Task Consume(ConsumeContext<EscrowDisputeResolvedEvent> c) =>
        Push(c.Message, nameof(EscrowDisputeResolvedEvent), $"Dispute resolved: {c.Message.Resolution}.", c.CancellationToken);

    public async Task Consume(ConsumeContext<EscrowWorkflowTransitioned> c)
    {
        var participants = await FindParticipants(c.Message.BookingId, c.CancellationToken);
        if (participants is null) return;

        await _notifier.WorkflowTransitionedAsync(
            c.Message.TenantId, c.Message.BookingId, participants.Value.BuyerId, participants.Value.ProviderId,
            c.Message.State, c.Message.Detail, c.Message.NextDeadlineUtc, c.CancellationToken);
    }

    private async Task Push(IDomainEvent evt, string eventType, string message, CancellationToken cancellationToken)
    {
        var participants = await FindParticipants(evt.BookingId, cancellationToken);
        if (participants is null) return;

        _logger.LogInformation("Outbox delivered {EventType} for booking {BookingId}", eventType, evt.BookingId);

        await _notifier.BookingUpdatedAsync(
            evt.TenantId, evt.BookingId, participants.Value.BuyerId, participants.Value.ProviderId,
            eventType, message, cancellationToken);
    }

    private async Task<(Guid BuyerId, Guid ProviderId)?> FindParticipants(Guid bookingId, CancellationToken cancellationToken)
    {
        // No tenant context in this consumer, so bypass the filter and read only the two ids.
        var row = await _db.Bookings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new { b.BuyerId, b.ProviderId })
            .FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : (row.BuyerId, row.ProviderId);
    }
}
