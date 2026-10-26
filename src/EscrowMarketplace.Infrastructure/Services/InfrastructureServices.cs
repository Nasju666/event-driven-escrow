using System.Text.Json;
using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

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

public class RedisCacheService : ICacheService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IDistributedCache cache, ILogger<RedisCacheService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var data = await _cache.GetStringAsync(key, cancellationToken);
            if (string.IsNullOrEmpty(data)) return default;
            return JsonSerializer.Deserialize<T>(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve cache key {Key}", key);
            return default;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration ?? TimeSpan.FromMinutes(10)
            };
            var json = JsonSerializer.Serialize(value);
            await _cache.SetStringAsync(key, json, options, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set cache key {Key}", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to remove cache key {Key}", key);
        }
    }
}

public class MockEscrowPaymentGateway : IEscrowPaymentGateway
{
    private readonly ILogger<MockEscrowPaymentGateway> _logger;

    public MockEscrowPaymentGateway(ILogger<MockEscrowPaymentGateway> logger)
    {
        _logger = logger;
    }

    public Task<(bool Success, string TransactionId, string? ErrorMessage)> AuthorizePaymentAsync(
        Guid buyerId, decimal amount, string currency, CancellationToken cancellationToken = default)
    {
        var authRef = $"auth_{Guid.NewGuid():N}_{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        _logger.LogInformation("Mock Payment Gateway authorized {Amount} {Currency} for Buyer {BuyerId}. Ref: {AuthRef}",
            amount, currency, buyerId, authRef);

        return Task.FromResult((true, authRef, (string?)null));
    }

    public Task<(bool Success, string? ErrorMessage)> CapturePaymentAsync(
        string transactionId, decimal amount, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock Payment Gateway captured {Amount} on Ref: {TransactionId}", amount, transactionId);
        return Task.FromResult((true, (string?)null));
    }

    public Task<(bool Success, string? ErrorMessage)> RefundPaymentAsync(
        string transactionId, decimal amount, string reason, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Mock Payment Gateway refunded {Amount} on Ref: {TransactionId}. Reason: {Reason}",
            amount, transactionId, reason);
        return Task.FromResult((true, (string?)null));
    }
}

public class BookingNotifier : IBookingNotifier
{
    private readonly IHubContext<MarketplaceBookingHub, IBookingClient> _hubContext;
    private readonly ILogger<BookingNotifier> _logger;

    public BookingNotifier(
        IHubContext<MarketplaceBookingHub, IBookingClient> hubContext,
        ILogger<BookingNotifier> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public async Task NotifyBookingCreatedAsync(Guid tenantId, Guid bookingId, object payload, CancellationToken cancellationToken = default)
    {
        var group = $"tenant:{tenantId}";
        _logger.LogInformation("SignalR Broadcasting BookingCreated to {Group}", group);
        await _hubContext.Clients.Group(group).BookingCreated(payload);
    }

    public async Task NotifyStatusChangedAsync(Guid tenantId, Guid bookingId, string status, string message, CancellationToken cancellationToken = default)
    {
        var group = $"tenant:{tenantId}";
        _logger.LogInformation("SignalR Broadcasting BookingStatusChanged ({Status}) to {Group}", status, group);
        await _hubContext.Clients.Group(group).BookingStatusChanged(bookingId.ToString(), status, message);
    }

    public async Task NotifyEscrowTransitionAsync(Guid tenantId, Guid escrowId, string status, decimal amount, CancellationToken cancellationToken = default)
    {
        var group = $"tenant:{tenantId}";
        _logger.LogInformation("SignalR Broadcasting EscrowStatusChanged ({Status}) to {Group}", status, group);
        await _hubContext.Clients.Group(group).EscrowStatusChanged(escrowId.ToString(), status, amount);
    }
}
