using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;

namespace EscrowMarketplace.Application.Common.Security;

/// <summary>
/// Resource-level authorization. Controllers enforce roles; handlers enforce
/// "is this your booking" checks, using identity from the token, never from the request body.
/// </summary>
public static class AccessExtensions
{
    public static Guid RequireTenantId(this ICurrentTenantService tenant) =>
        tenant.TenantId ?? throw new ForbiddenAccessException("A tenant context is required.");

    public static Guid RequireUserId(this ICurrentActor actor) =>
        actor.UserId ?? throw new ForbiddenAccessException("An authenticated user is required.");

    public static bool IsAdmin(this ICurrentActor actor) =>
        actor.Role is UserRole.Admin or UserRole.Manager;

    public static Guid RequireRole(this ICurrentActor actor, UserRole role)
    {
        var userId = actor.RequireUserId();
        if (actor.Role != role)
            throw new ForbiddenAccessException($"This action requires the {role} role.");
        return userId;
    }

    public static void RequireSystem(this ICurrentActor actor)
    {
        if (!actor.IsSystem)
            throw new ForbiddenAccessException("This operation can only be performed by the workflow engine.");
    }

    /// <summary>
    /// Participants and admins can see a booking. Everyone else gets 404 rather than 403,
    /// so the API doesn't confirm that a booking id exists.
    /// </summary>
    public static void EnsureCanView(this ICurrentActor actor, Guid bookingId, Guid buyerId, Guid providerId)
    {
        if (actor.IsSystem || actor.IsAdmin()) return;

        var userId = actor.RequireUserId();
        if (userId != buyerId && userId != providerId)
            throw new NotFoundException(nameof(Booking), bookingId);
    }

    public static void EnsureCanView(this ICurrentActor actor, Booking booking) =>
        actor.EnsureCanView(booking.Id, booking.BuyerId, booking.ProviderId);

    public static Guid EnsureIsBuyerOf(this ICurrentActor actor, Booking booking)
    {
        actor.EnsureCanView(booking);
        var userId = actor.RequireUserId();
        if (booking.BuyerId != userId)
            throw new ForbiddenAccessException("Only the buyer of this booking can do this.");
        return userId;
    }

    public static Guid EnsureIsProviderOf(this ICurrentActor actor, Booking booking)
    {
        actor.EnsureCanView(booking);
        var userId = actor.RequireUserId();
        if (booking.ProviderId != userId)
            throw new ForbiddenAccessException("Only the provider of this booking can do this.");
        return userId;
    }
}
