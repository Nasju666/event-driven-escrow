using EscrowMarketplace.Domain.Events;

namespace EscrowMarketplace.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    private readonly List<IDomainEvent> _domainEvents = new();

    /// <summary>
    /// Events raised by state changes on this entity. The DbContext collects them on SaveChanges
    /// and publishes them through the MassTransit transactional outbox, so each event is stored
    /// in the same database transaction as the state change that produced it.
    /// </summary>
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void AddDomainEvent(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

public interface ITenantEntity
{
    public Guid TenantId { get; set; }
}

public interface IAuditableEntity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
