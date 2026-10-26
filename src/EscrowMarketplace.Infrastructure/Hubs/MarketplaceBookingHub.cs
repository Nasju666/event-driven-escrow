using System.Security.Claims;
using EscrowMarketplace.Application.Common.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace EscrowMarketplace.Infrastructure.Hubs;

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
        var httpContext = Context.GetHttpContext();
        var user = Context.User;

        // Extract tenant ID from claim or query string
        var tenantId = user?.FindFirst("tenant_id")?.Value 
            ?? httpContext?.Request.Query["tenant_id"].ToString();

        var userId = user?.FindFirst(ClaimTypes.NameIdentifier)?.Value 
            ?? user?.FindFirst("sub")?.Value 
            ?? httpContext?.Request.Query["user_id"].ToString();

        if (!string.IsNullOrEmpty(tenantId))
        {
            var tenantGroup = $"tenant:{tenantId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, tenantGroup);
            _logger.LogInformation("SignalR Connection {ConnectionId} joined {Group}", Context.ConnectionId, tenantGroup);
        }

        if (!string.IsNullOrEmpty(userId))
        {
            var userGroup = $"user:{userId}";
            await Groups.AddToGroupAsync(Context.ConnectionId, userGroup);
            _logger.LogInformation("SignalR Connection {ConnectionId} joined {Group}", Context.ConnectionId, userGroup);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR Connection {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeToBooking(string bookingId)
    {
        var group = $"booking:{bookingId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
        _logger.LogInformation("Connection {ConnectionId} subscribed to {Group}", Context.ConnectionId, group);
    }

    public async Task UnsubscribeFromBooking(string bookingId)
    {
        var group = $"booking:{bookingId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
    }

    public async Task SubscribeToResource(string resourceId)
    {
        var group = $"resource:{resourceId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, group);
    }
}
