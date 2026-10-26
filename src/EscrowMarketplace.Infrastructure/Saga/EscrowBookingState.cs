using MassTransit;

namespace EscrowMarketplace.Infrastructure.Saga;

public class EscrowBookingState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;

    public Guid BookingId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ResourceId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid ProviderId { get; set; }

    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// PostgreSQL xmin optimistic concurrency token.
    /// Allows MassTransit to detect concurrent state transitions and auto-retry without deadlocks.
    /// </summary>
    public uint RowVersion { get; set; }
}
