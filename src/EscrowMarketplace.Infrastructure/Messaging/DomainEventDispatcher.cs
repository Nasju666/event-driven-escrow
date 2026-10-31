using EscrowMarketplace.Domain.Events;
using MassTransit;

namespace EscrowMarketplace.Infrastructure.Messaging;

public interface IDomainEventDispatcher
{
    /// <summary>
    /// True when publishing writes to the EF transactional outbox, so events must be published
    /// BEFORE SaveChanges in order to commit atomically with the data.
    /// </summary>
    bool UsesTransactionalOutbox { get; }

    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken);
}

/// <summary>
/// Publishes domain events with the scoped MassTransit IPublishEndpoint. In a web request that is
/// the EF bus outbox; inside a consumer it is the consume context, which uses the consumer outbox.
/// Either way the events are persisted with the transaction and delivered after commit.
/// </summary>
public class MassTransitDomainEventDispatcher : IDomainEventDispatcher
{
    private readonly IPublishEndpoint _publishEndpoint;

    public MassTransitDomainEventDispatcher(IPublishEndpoint publishEndpoint, bool usesTransactionalOutbox)
    {
        _publishEndpoint = publishEndpoint;
        UsesTransactionalOutbox = usesTransactionalOutbox;
    }

    public bool UsesTransactionalOutbox { get; }

    public async Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken cancellationToken)
    {
        foreach (var domainEvent in domainEvents)
        {
            // Publish with the runtime type so consumers of e.g. BookingPaidEvent receive it,
            // rather than publishing it as the IDomainEvent interface.
            await _publishEndpoint.Publish(domainEvent, domainEvent.GetType(), cancellationToken);
        }
    }
}
