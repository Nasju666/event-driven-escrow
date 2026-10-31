using System.Security.Claims;
using EscrowMarketplace.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Hubs;

public static class HubGroups
{
    public static string User(Guid userId) => $"user:{userId}";
    public static string TenantAdmins(Guid tenantId) => $"tenant:{tenantId}:admins";
}

/// <summary>
/// Push-only hub. Group membership comes only from the validated JWT (no query-string fallbacks
/// and no "subscribe to any booking id"), so a client can only receive updates for its own
/// bookings, or for its tenant if it is an admin.
/// </summary>
[Authorize]
public class MarketplaceBookingHub : Hub<IBookingClient>
{
    private readonly ILogger<MarketplaceBookingHub> _logger;

    public MarketplaceBookingHub(ILogger<MarketplaceBookingHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        var userIdValue = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user?.FindFirst("sub")?.Value;
        var tenantIdValue = user?.FindFirst("tenant_id")?.Value;

        if (!Guid.TryParse(userIdValue, out var userId) || !Guid.TryParse(tenantIdValue, out var tenantId))
        {
            _logger.LogWarning("Rejecting SignalR connection {ConnectionId}: token is missing user or tenant claims.", Context.ConnectionId);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(userId));

        if (user!.IsInRole("Admin") || user.IsInRole("Manager"))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.TenantAdmins(tenantId));
        }

        await base.OnConnectedAsync();
    }
}
