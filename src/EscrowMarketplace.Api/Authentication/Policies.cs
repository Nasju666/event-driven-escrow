using EscrowMarketplace.Domain.Enums;

namespace EscrowMarketplace.Api.Authentication;

/// <summary>
/// Role-based policies. Every endpoint requires an authenticated user by default (fallback
/// policy). These narrow it further, and handlers add per-booking ownership checks on top.
/// </summary>
public static class Policies
{
    public const string Buyer = "BuyerOnly";
    public const string Provider = "ProviderOnly";
    public const string Admin = "AdminOnly";

    public static readonly string BuyerRole = nameof(UserRole.Buyer);
    public static readonly string ProviderRole = nameof(UserRole.Provider);
    public static readonly string[] AdminRoles = { nameof(UserRole.Admin), nameof(UserRole.Manager) };
}

public static class RateLimitPolicies
{
    public const string PerUser = "PerUserSlidingWindow";
    public const string Checkout = "CheckoutConcurrency";
}
