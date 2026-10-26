using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Infrastructure.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Consumers;

public class EscrowStatusChangedSignalRConsumer : IConsumer<EscrowStatusChangedIntegrationEvent>
{
    private readonly IHubContext<MarketplaceBookingHub, IBookingClient> _hubContext;
    private readonly ILogger<EscrowStatusChangedSignalRConsumer> _logger;

    public EscrowStatusChangedSignalRConsumer(
        IHubContext<MarketplaceBookingHub, IBookingClient> hubContext,
        ILogger<EscrowStatusChangedSignalRConsumer> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<EscrowStatusChangedIntegrationEvent> context)
    {
        var msg = context.Message;
        _logger.LogInformation("Processing SignalR push for Booking {BookingId}: Status changed to {Status}",
            msg.BookingId, msg.NewStatus);

        // 1. Notify the tenant group (all connected operators/dashboards)
        await _hubContext.Clients.Group($"tenant:{msg.TenantId}")
            .BookingStatusChanged(msg.BookingId.ToString(), msg.NewStatus, msg.Message);

        // 2. Notify the buyer specifically
        await _hubContext.Clients.Group($"user:{msg.BuyerId}")
            .EscrowStatusChanged(msg.EscrowAccountId.ToString(), msg.NewStatus, 0m);

        // 3. Notify the provider specifically
        await _hubContext.Clients.Group($"user:{msg.ProviderId}")
            .EscrowStatusChanged(msg.EscrowAccountId.ToString(), msg.NewStatus, 0m);

        // 4. Notify any live subscriber to this specific booking stream
        await _hubContext.Clients.Group($"booking:{msg.BookingId}")
            .BookingStatusChanged(msg.BookingId.ToString(), msg.NewStatus, msg.Message);
    }
}

public class BookingEventAuditConsumer : 
    IConsumer<BookingSlotReservedEvent>,
    IConsumer<EscrowFundsAuthorizedEvent>,
    IConsumer<EscrowPayoutReleasedEvent>
{
    private readonly ILogger<BookingEventAuditConsumer> _logger;

    public BookingEventAuditConsumer(ILogger<BookingEventAuditConsumer> logger)
    {
        _logger = logger;
    }

    public Task Consume(ConsumeContext<BookingSlotReservedEvent> context)
    {
        _logger.LogInformation("Outbox Dispatched [BookingSlotReservedEvent]: Booking {BookingId}, Amount: {Amount}",
            context.Message.BookingId, context.Message.TotalAmount);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<EscrowFundsAuthorizedEvent> context)
    {
        _logger.LogInformation("Outbox Dispatched [EscrowFundsAuthorizedEvent]: Escrow {EscrowId}, Ref: {Ref}",
            context.Message.EscrowAccountId, context.Message.PaymentReference);
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<EscrowPayoutReleasedEvent> context)
    {
        _logger.LogInformation("Outbox Dispatched [EscrowPayoutReleasedEvent]: Payout {Payout} to Provider {ProviderId}",
            context.Message.ProviderPayoutAmount, context.Message.ProviderId);
        return Task.CompletedTask;
    }
}
