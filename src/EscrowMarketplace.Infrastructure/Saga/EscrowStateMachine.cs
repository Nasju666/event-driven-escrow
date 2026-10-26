using EscrowMarketplace.Domain.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Saga;

public class EscrowStateMachine : MassTransitStateMachine<EscrowBookingState>
{
    private readonly ILogger<EscrowStateMachine> _logger;

    public State AwaitingPayment { get; private set; } = null!;
    public State FundsEscrowedAwaitingProvider { get; private set; } = null!;
    public State ConfirmedInEscrow { get; private set; } = null!;
    public State ServiceCompletedState { get; private set; } = null!;
    public State Settled { get; private set; } = null!;
    public State Disputed { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;
    public State Refunded { get; private set; } = null!;

    public Event<BookingSlotReservedEvent> BookingSlotReserved { get; private set; } = null!;
    public Event<EscrowFundsAuthorizedEvent> EscrowFundsAuthorized { get; private set; } = null!;
    public Event<ProviderConfirmedBookingEvent> ProviderConfirmed { get; private set; } = null!;
    public Event<ServiceCompletedEvent> ServiceCompleted { get; private set; } = null!;
    public Event<EscrowPayoutReleasedEvent> EscrowPayoutReleased { get; private set; } = null!;
    public Event<BookingCancelledEvent> BookingCancelled { get; private set; } = null!;
    public Event<EscrowDisputeRaisedEvent> EscrowDisputeRaised { get; private set; } = null!;
    public Event<EscrowDisputeResolvedEvent> EscrowDisputeResolved { get; private set; } = null!;

    public EscrowStateMachine(ILogger<EscrowStateMachine> logger)
    {
        _logger = logger;

        InstanceState(x => x.CurrentState);

        Event(() => BookingSlotReserved, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => EscrowFundsAuthorized, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => ProviderConfirmed, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => ServiceCompleted, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => EscrowPayoutReleased, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => BookingCancelled, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => EscrowDisputeRaised, x => x.CorrelateById(m => m.Message.BookingId));
        Event(() => EscrowDisputeResolved, x => x.CorrelateById(m => m.Message.BookingId));

        Initially(
            When(BookingSlotReserved)
                .Then(context =>
                {
                    context.Saga.CorrelationId = context.Message.BookingId;
                    context.Saga.BookingId = context.Message.BookingId;
                    context.Saga.TenantId = context.Message.TenantId;
                    context.Saga.ResourceId = context.Message.ResourceId;
                    context.Saga.BuyerId = context.Message.BuyerId;
                    context.Saga.ProviderId = context.Message.ProviderId;
                    context.Saga.Amount = context.Message.TotalAmount;
                    context.Saga.Currency = context.Message.Currency;
                    context.Saga.CreatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Escrow Saga started for Booking {BookingId}. Awaiting Payment Authorization.", context.Message.BookingId);
                })
                .TransitionTo(AwaitingPayment)
        );

        During(AwaitingPayment,
            When(EscrowFundsAuthorized)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Escrow funds authorized for Booking {BookingId}. Transitioning to FundsEscrowedAwaitingProvider.", context.Saga.BookingId);
                })
                .TransitionTo(FundsEscrowedAwaitingProvider),
            When(BookingCancelled)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Booking {BookingId} cancelled during payment authorization.", context.Saga.BookingId);
                })
                .TransitionTo(Cancelled)
        );

        During(FundsEscrowedAwaitingProvider,
            When(ProviderConfirmed)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Provider confirmed Booking {BookingId}. Transitioning to ConfirmedInEscrow.", context.Saga.BookingId);
                })
                .TransitionTo(ConfirmedInEscrow),
            When(BookingCancelled)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Booking {BookingId} cancelled. Transitioning to Refunded.", context.Saga.BookingId);
                })
                .TransitionTo(Refunded)
        );

        During(ConfirmedInEscrow,
            When(ServiceCompleted)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Service completed for Booking {BookingId}. Ready for escrow payout release.", context.Saga.BookingId);
                })
                .TransitionTo(ServiceCompletedState),
            When(EscrowDisputeRaised)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning("Dispute raised for Booking {BookingId}. Transitioning to Disputed.", context.Saga.BookingId);
                })
                .TransitionTo(Disputed),
            When(BookingCancelled)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Confirmed booking {BookingId} cancelled. Transitioning to Refunded.", context.Saga.BookingId);
                })
                .TransitionTo(Refunded)
        );

        During(ServiceCompletedState,
            When(EscrowPayoutReleased)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Escrow payout released for Booking {BookingId}. State Settled.", context.Saga.BookingId);
                })
                .TransitionTo(Settled),
            When(EscrowDisputeRaised)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogWarning("Post-service dispute raised for Booking {BookingId}.", context.Saga.BookingId);
                })
                .TransitionTo(Disputed)
        );

        During(Disputed,
            When(EscrowDisputeResolved)
                .Then(context =>
                {
                    context.Saga.UpdatedAt = DateTimeOffset.UtcNow;
                    _logger.LogInformation("Dispute resolved for Booking {BookingId}. Resolution: {Resolution}", context.Saga.BookingId, context.Message.Resolution);
                })
                .TransitionTo(Settled)
        );
    }
}
