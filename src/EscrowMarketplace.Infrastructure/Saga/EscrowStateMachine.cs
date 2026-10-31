using System.Linq.Expressions;
using EscrowMarketplace.Domain.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Saga;

/// <summary>
/// Escrow workflow orchestrator. The aggregates own the business rules: every user action is a
/// synchronous command that validates and changes state. The saga owns TIME and coordination:
/// it listens to the domain events, tracks where each booking is, and schedules a timeout for
/// every deadline. When one fires it issues a command that runs as the system actor:
///
///   AwaitingPayment          --payment window passes-->     expire booking, void escrow
///   AwaitingProviderResponse --provider never answers-->    cancel and refund the buyer
///   InspectionWindow         --buyer neither approves
///                              nor disputes in time-->      release payout to the provider
///
/// Terminal states (Settled, Refunded, Cancelled, Expired) are kept, not deleted, so the
/// workflow history of a booking can still be inspected.
/// </summary>
public class EscrowStateMachine : MassTransitStateMachine<EscrowBookingState>
{
    private readonly ILogger<EscrowStateMachine> _logger;

    public State AwaitingPayment { get; private set; } = null!;
    public State AwaitingProviderResponse { get; private set; } = null!;
    public State Confirmed { get; private set; } = null!;
    public State InspectionWindow { get; private set; } = null!;
    public State Disputed { get; private set; } = null!;
    public State Settled { get; private set; } = null!;
    public State Refunded { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;
    public State Expired { get; private set; } = null!;

    public Event<BookingSlotReservedEvent> SlotReserved { get; private set; } = null!;
    public Event<BookingPaidEvent> Paid { get; private set; } = null!;
    public Event<ProviderConfirmedBookingEvent> ProviderAccepted { get; private set; } = null!;
    public Event<ServiceCompletedEvent> ServiceCompleted { get; private set; } = null!;
    public Event<EscrowDisputeRaisedEvent> DisputeRaised { get; private set; } = null!;
    public Event<EscrowPayoutReleasedEvent> PayoutReleased { get; private set; } = null!;
    public Event<EscrowRefundedEvent> RefundIssued { get; private set; } = null!;
    public Event<BookingCancelledEvent> BookingCancelled { get; private set; } = null!;
    public Event<BookingExpiredEvent> BookingExpired { get; private set; } = null!;

    public Schedule<EscrowBookingState, PaymentTimeoutExpired> PaymentTimeout { get; private set; } = null!;
    public Schedule<EscrowBookingState, ProviderResponseTimeoutExpired> ProviderResponseTimeout { get; private set; } = null!;
    public Schedule<EscrowBookingState, InspectionWindowElapsed> InspectionTimeout { get; private set; } = null!;

    public EscrowStateMachine(ILogger<EscrowStateMachine> logger)
    {
        _logger = logger;

        InstanceState(x => x.CurrentState);

        Event(() => SlotReserved, x => x.CorrelateById(ctx => ctx.Message.BookingId));
        CorrelateByBooking(() => Paid);
        CorrelateByBooking(() => ProviderAccepted);
        CorrelateByBooking(() => ServiceCompleted);
        CorrelateByBooking(() => DisputeRaised);
        CorrelateByBooking(() => PayoutReleased);
        CorrelateByBooking(() => RefundIssued);
        CorrelateByBooking(() => BookingCancelled);
        CorrelateByBooking(() => BookingExpired);

        Schedule(() => PaymentTimeout, s => s.PaymentTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromMinutes(15); // fallback only; the real deadline comes from the event
            s.Received = r =>
            {
                r.CorrelateById(ctx => ctx.Message.BookingId);
                r.OnMissingInstance(m => m.Discard());
            };
        });

        Schedule(() => ProviderResponseTimeout, s => s.ProviderResponseTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromHours(24);
            s.Received = r =>
            {
                r.CorrelateById(ctx => ctx.Message.BookingId);
                r.OnMissingInstance(m => m.Discard());
            };
        });

        Schedule(() => InspectionTimeout, s => s.InspectionTimeoutTokenId, s =>
        {
            s.Delay = TimeSpan.FromHours(72);
            s.Received = r =>
            {
                r.CorrelateById(ctx => ctx.Message.BookingId);
                r.OnMissingInstance(m => m.Discard());
            };
        });

        // Events can legitimately arrive in a state that no longer cares about them
        // (e.g. BookingCancelled after the refund already moved the saga to Refunded).
        OnUnhandledEvent(x => x.Ignore());

        Initially(
            When(SlotReserved)
                .Then(ctx =>
                {
                    var m = ctx.Message;
                    ctx.Saga.TenantId = m.TenantId;
                    ctx.Saga.BuyerId = m.BuyerId;
                    ctx.Saga.ProviderId = m.ProviderId;
                    ctx.Saga.Amount = m.TotalAmount;
                    ctx.Saga.Currency = m.Currency;
                    ctx.Saga.CreatedAt = DateTimeOffset.UtcNow;
                    SetDeadline(ctx.Saga, m.PaymentDueUtc, "PaymentDue");
                })
                .Schedule(PaymentTimeout,
                    ctx => new PaymentTimeoutExpired(ctx.Message.BookingId),
                    ctx => Until(ctx.Message.PaymentDueUtc))
                .TransitionTo(AwaitingPayment)
                .NotifyTransition("AwaitingPayment", "Slot reserved. Waiting for the buyer to pay."));

        During(AwaitingPayment,
            When(Paid)
                .Unschedule(PaymentTimeout)
                .Then(ctx => SetDeadline(ctx.Saga, ctx.Message.ProviderResponseDueUtc, "ProviderResponseDue"))
                .Schedule(ProviderResponseTimeout,
                    ctx => new ProviderResponseTimeoutExpired(ctx.Message.BookingId),
                    ctx => Until(ctx.Message.ProviderResponseDueUtc))
                .TransitionTo(AwaitingProviderResponse)
                .NotifyTransition("AwaitingProviderResponse", "Funds held in escrow. Waiting for the provider to accept."),

            When(PaymentTimeout.Received)
                .Then(ctx => _logger.LogInformation("Payment window elapsed for booking {BookingId}; requesting expiry.", ctx.Saga.CorrelationId))
                .Publish(ctx => new ExpireUnpaidBooking(ctx.Saga.CorrelationId, ctx.Saga.TenantId)),

            When(BookingCancelled)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Cancelled)
                .NotifyTransition("Cancelled", "Cancelled before payment. Nothing to refund."));

        During(AwaitingProviderResponse,
            When(ProviderAccepted)
                .Unschedule(ProviderResponseTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Confirmed)
                .NotifyTransition("Confirmed", "Provider accepted. Funds stay in escrow until the session is done."),

            When(ProviderResponseTimeout.Received)
                .Then(ctx => _logger.LogInformation("Provider did not respond for booking {BookingId}; requesting cancellation and refund.", ctx.Saga.CorrelationId))
                .Publish(ctx => new CancelUnconfirmedBooking(ctx.Saga.CorrelationId, ctx.Saga.TenantId)),

            When(BookingCancelled)
                .Unschedule(ProviderResponseTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Cancelled)
                .NotifyTransition("Cancelled", "Cancelled after payment. Refund in progress."));

        During(Confirmed,
            When(ServiceCompleted)
                .Then(ctx => SetDeadline(ctx.Saga, ctx.Message.InspectionDeadlineUtc, "AutoRelease"))
                .Schedule(InspectionTimeout,
                    ctx => new InspectionWindowElapsed(ctx.Message.BookingId),
                    ctx => Until(ctx.Message.InspectionDeadlineUtc))
                .TransitionTo(InspectionWindow)
                .NotifyTransition("InspectionWindow", "Session marked complete. Payout releases automatically unless the buyer disputes."),

            When(DisputeRaised)
                .TransitionTo(Disputed)
                .NotifyTransition("Disputed", "Dispute raised. Funds frozen until an admin decides."),

            When(BookingCancelled)
                .TransitionTo(Cancelled)
                .NotifyTransition("Cancelled", "Cancelled after confirmation. Refund in progress."));

        During(InspectionWindow,
            When(InspectionTimeout.Received)
                .Then(ctx => _logger.LogInformation("Inspection window elapsed for booking {BookingId}; requesting automatic payout.", ctx.Saga.CorrelationId))
                .Publish(ctx => new ReleaseEscrowAfterInspection(ctx.Saga.CorrelationId, ctx.Saga.TenantId)),

            When(DisputeRaised)
                .Unschedule(InspectionTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Disputed)
                .NotifyTransition("Disputed", "Dispute raised during the inspection window. Automatic payout cancelled."));

        // Money-final events are accepted from ANY state. The aggregate has already decided (the
        // money moved in the same transaction that raised the event), so the saga just records
        // the outcome and cancels whatever timeouts are pending. That makes the saga correct even
        // if events overtake each other in transit, e.g. a fast "approve" whose
        // EscrowPayoutReleasedEvent is processed before the ServiceCompletedEvent.
        DuringAny(
            When(PayoutReleased)
                .Unschedule(PaymentTimeout)
                .Unschedule(ProviderResponseTimeout)
                .Unschedule(InspectionTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Settled)
                .NotifyTransition("Settled", "Payout released to the provider. Escrow closed."),

            When(RefundIssued)
                .Unschedule(PaymentTimeout)
                .Unschedule(ProviderResponseTimeout)
                .Unschedule(InspectionTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Refunded)
                .NotifyTransition("Refunded", "Full refund returned to the buyer. Escrow closed."),

            When(BookingExpired)
                .Unschedule(PaymentTimeout)
                .Then(ctx => ClearDeadline(ctx.Saga))
                .TransitionTo(Expired)
                .NotifyTransition("Expired", "Payment window elapsed. Slot released."));
    }

    /// <summary>
    /// Correlates on BookingId. If the saga row does not exist yet (the event overtook
    /// BookingSlotReservedEvent), fault so the endpoint's retry policy redelivers it shortly.
    /// </summary>
    private void CorrelateByBooking<T>(Expression<Func<Event<T>>> eventProperty)
        where T : class, IDomainEvent
    {
        Event(eventProperty, x =>
        {
            x.CorrelateById(ctx => ctx.Message.BookingId);
            x.OnMissingInstance(m => m.Fault());
        });
    }

    private static TimeSpan Until(DateTimeOffset deadlineUtc)
    {
        var delay = deadlineUtc - DateTimeOffset.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    private static void SetDeadline(EscrowBookingState saga, DateTimeOffset deadlineUtc, string kind)
    {
        saga.NextDeadlineUtc = deadlineUtc;
        saga.NextDeadlineKind = kind;
        saga.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearDeadline(EscrowBookingState saga)
    {
        saga.NextDeadlineUtc = null;
        saga.NextDeadlineKind = null;
        saga.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

internal static class EscrowWorkflowBinderExtensions
{
    /// <summary>Publishes an EscrowWorkflowTransitioned event (shown live in the dashboard's activity feed).</summary>
    public static EventActivityBinder<EscrowBookingState, TMessage> NotifyTransition<TMessage>(
        this EventActivityBinder<EscrowBookingState, TMessage> binder, string state, string detail)
        where TMessage : class
    {
        return binder.Publish(ctx => new EscrowWorkflowTransitioned(
            ctx.Saga.CorrelationId,
            ctx.Saga.TenantId,
            state,
            detail,
            ctx.Saga.NextDeadlineUtc,
            DateTimeOffset.UtcNow));
    }
}
