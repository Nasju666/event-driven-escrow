using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.ValueObjects;

namespace EscrowMarketplace.UnitTests.Domain;

internal static class TestData
{
    public static readonly Guid TenantId = Guid.NewGuid();

    public static User Buyer(decimal wallet = 1000m) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, Email = "buyer@test", FullName = "Buyer", Role = UserRole.Buyer, WalletBalance = wallet
    };

    public static User Provider(decimal wallet = 0m) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, Email = "provider@test", FullName = "Provider", Role = UserRole.Provider, WalletBalance = wallet
    };

    public static BookableResource Resource(Guid providerId, decimal hourlyRate = 50m) => new()
    {
        Id = Guid.NewGuid(), TenantId = TenantId, ProviderId = providerId, Title = "Room", HourlyRate = hourlyRate, Currency = "USD"
    };

    public static TimeRange Period(double hours = 2)
    {
        var start = DateTimeOffset.UtcNow.AddDays(2);
        return new TimeRange(start, start.AddHours(hours));
    }

    /// <summary>A reserved booking with its escrow (10% commission), not yet paid.</summary>
    public static (Booking Booking, EscrowAccount Escrow, User Buyer, User Provider) ReservedBooking(
        decimal hourlyRate = 50m, double hours = 2, decimal buyerWallet = 1000m)
    {
        var buyer = Buyer(buyerWallet);
        var provider = Provider();
        var resource = Resource(provider.Id, hourlyRate);

        var booking = Booking.Reserve(TenantId, resource, buyer.Id, Period(hours), DateTimeOffset.UtcNow.AddMinutes(15));
        var escrow = EscrowAccount.Create(TenantId, booking.Id, buyer.Id, provider.Id, booking.TotalAmount, 10m);
        booking.EscrowAccount = escrow;

        return (booking, escrow, buyer, provider);
    }

    /// <summary>Same as the PayForBooking handler: buyer debited, escrow captured, booking Held.</summary>
    public static (Booking Booking, EscrowAccount Escrow, User Buyer, User Provider) PaidBooking(
        decimal hourlyRate = 50m, double hours = 2, decimal buyerWallet = 1000m)
    {
        var data = ReservedBooking(hourlyRate, hours, buyerWallet);
        data.Buyer.Debit(data.Escrow.TotalAmount);
        data.Escrow.Authorize("test_ref");
        data.Escrow.Capture();
        data.Booking.MarkPaid(DateTimeOffset.UtcNow.AddHours(1));
        return data;
    }
}
