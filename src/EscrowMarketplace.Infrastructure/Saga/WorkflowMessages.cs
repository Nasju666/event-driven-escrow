namespace EscrowMarketplace.Infrastructure.Saga;

// Scheduled timeout messages. The saga schedules them for a deadline and cancels them
// (unschedules) if the user acts first.
public record PaymentTimeoutExpired(Guid BookingId);
public record ProviderResponseTimeoutExpired(Guid BookingId);
public record InspectionWindowElapsed(Guid BookingId);

// Commands the saga issues when a timeout fires. WorkflowCommandConsumer runs them as the
// system actor through the normal MediatR handlers, so the domain rules still apply.
public record ExpireUnpaidBooking(Guid BookingId, Guid TenantId);
public record CancelUnconfirmedBooking(Guid BookingId, Guid TenantId);
public record ReleaseEscrowAfterInspection(Guid BookingId, Guid TenantId);

/// <summary>Published on every saga transition so the dashboard can show what the workflow is doing.</summary>
public record EscrowWorkflowTransitioned(
    Guid BookingId,
    Guid TenantId,
    string State,
    string Detail,
    DateTimeOffset? NextDeadlineUtc,
    DateTimeOffset OccurredAt);
