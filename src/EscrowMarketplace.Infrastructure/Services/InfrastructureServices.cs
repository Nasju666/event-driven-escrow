using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Infrastructure.Hubs;
using EscrowMarketplace.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace EscrowMarketplace.Infrastructure.Services;

public class CurrentTenantService : ICurrentTenantService
{
    public Guid? TenantId { get; private set; }
    public string? TenantIdentifier { get; private set; }

    public void SetTenant(Guid tenantId, string identifier)
    {
        TenantId = tenantId;
        TenantIdentifier = identifier;
    }
}

public class CurrentActor : ICurrentActor
{
    public Guid? UserId { get; private set; }
    public UserRole? Role { get; private set; }
    public bool IsSystem { get; private set; }

    public void SetUser(Guid userId, UserRole role)
    {
        UserId = userId;
        Role = role;
        IsSystem = false;
    }

    public void SetSystem()
    {
        UserId = null;
        Role = null;
        IsSystem = true;
    }
}

/// <summary>Message pushed to browsers. Kind is "event" (domain event) or "workflow" (saga transition).</summary>
public record BookingUpdateMessage(
    string BookingId,
    string Kind,
    string Name,
    string Message,
    DateTimeOffset? NextDeadlineUtc,
    DateTimeOffset At);

public interface IBookingClient
{
    Task BookingUpdated(BookingUpdateMessage update);
}

public interface IBookingNotifier
{
    Task BookingUpdatedAsync(Guid tenantId, Guid bookingId, Guid buyerId, Guid providerId,
        string eventType, string message, CancellationToken cancellationToken = default);

    Task WorkflowTransitionedAsync(Guid tenantId, Guid bookingId, Guid buyerId, Guid providerId,
        string state, string detail, DateTimeOffset? nextDeadlineUtc, CancellationToken cancellationToken = default);
}

public class BookingNotifier : IBookingNotifier
{
    private readonly IHubContext<MarketplaceBookingHub, IBookingClient> _hubContext;

    public BookingNotifier(IHubContext<MarketplaceBookingHub, IBookingClient> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task BookingUpdatedAsync(Guid tenantId, Guid bookingId, Guid buyerId, Guid providerId,
        string eventType, string message, CancellationToken cancellationToken = default)
    {
        var update = new BookingUpdateMessage(bookingId.ToString(), "event", eventType, message, null, DateTimeOffset.UtcNow);
        return _hubContext.Clients.Groups(Audience(tenantId, buyerId, providerId)).BookingUpdated(update);
    }

    public Task WorkflowTransitionedAsync(Guid tenantId, Guid bookingId, Guid buyerId, Guid providerId,
        string state, string detail, DateTimeOffset? nextDeadlineUtc, CancellationToken cancellationToken = default)
    {
        var update = new BookingUpdateMessage(bookingId.ToString(), "workflow", state, detail, nextDeadlineUtc, DateTimeOffset.UtcNow);
        return _hubContext.Clients.Groups(Audience(tenantId, buyerId, providerId)).BookingUpdated(update);
    }

    private static IReadOnlyList<string> Audience(Guid tenantId, Guid buyerId, Guid providerId) => new[]
    {
        HubGroups.User(buyerId),
        HubGroups.User(providerId),
        HubGroups.TenantAdmins(tenantId)
    };
}

public class EscrowWorkflowReader : IEscrowWorkflowReader
{
    private readonly MarketplaceDbContext _context;

    public EscrowWorkflowReader(MarketplaceDbContext context)
    {
        _context = context;
    }

    public async Task<WorkflowStateDto?> GetAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        // In in-memory demo mode the saga uses MassTransit's in-memory repository, not this table.
        if (!_context.Database.IsRelational())
            return null;

        return await _context.EscrowWorkflows
            .AsNoTracking()
            .Where(s => s.CorrelationId == bookingId)
            .Select(s => new WorkflowStateDto(
                s.CorrelationId, s.CurrentState, s.CreatedAt, s.UpdatedAt, s.NextDeadlineUtc, s.NextDeadlineKind))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
