using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EscrowMarketplace.Infrastructure.Saga;

/// <summary>
/// Persisted state of one escrow workflow. CorrelationId is the BookingId.
/// Instances are kept after they reach a terminal state, so the workflow history stays queryable.
/// </summary>
public class EscrowBookingState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = string.Empty;

    public Guid TenantId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid ProviderId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>The deadline the saga is currently waiting on, and what happens when it passes.</summary>
    public DateTimeOffset? NextDeadlineUtc { get; set; }
    public string? NextDeadlineKind { get; set; }

    // Tokens of the scheduled timeout messages, so they can be cancelled when the user acts first.
    public Guid? PaymentTimeoutTokenId { get; set; }
    public Guid? ProviderResponseTimeoutTokenId { get; set; }
    public Guid? InspectionTimeoutTokenId { get; set; }

    /// <summary>xmin concurrency token: two events for the same booking can't both apply a transition.</summary>
    public uint RowVersion { get; set; }
}

public class EscrowBookingStateMap : SagaClassMap<EscrowBookingState>
{
    private readonly bool _useXminConcurrency;

    public EscrowBookingStateMap(bool useXminConcurrency)
    {
        _useXminConcurrency = useXminConcurrency;
    }

    protected override void Configure(EntityTypeBuilder<EscrowBookingState> entity, ModelBuilder model)
    {
        entity.ToTable("EscrowWorkflows");
        entity.Property(x => x.CurrentState).HasMaxLength(64).IsRequired();
        entity.Property(x => x.Currency).HasMaxLength(10);
        entity.Property(x => x.Amount).HasPrecision(18, 2);
        entity.Property(x => x.NextDeadlineKind).HasMaxLength(64);
        entity.HasIndex(x => new { x.TenantId, x.CurrentState });

        if (_useXminConcurrency)
            entity.Property(x => x.RowVersion).IsRowVersion();
        else
            entity.Ignore(x => x.RowVersion);
    }
}
