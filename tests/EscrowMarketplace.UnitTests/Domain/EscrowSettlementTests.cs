using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.Services;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

/// <summary>Money is conserved: whatever leaves the buyer ends up with the provider and platform, or back with the buyer.</summary>
public class EscrowSettlementTests
{
    [Fact]
    public void ReleaseToProvider_CreditsPayout_AndSettlesBooking()
    {
        var (booking, escrow, buyer, provider) = TestData.PaidBooking(hourlyRate: 50m, hours: 2, buyerWallet: 1000m);
        booking.Confirm();
        booking.Complete(DateTimeOffset.UtcNow.AddDays(3));

        EscrowSettlement.ReleaseToProvider(booking, escrow, provider);

        booking.Status.Should().Be(BookingStatus.Settled);
        escrow.Status.Should().Be(EscrowStatus.Settled);
        buyer.WalletBalance.Should().Be(900m);
        provider.WalletBalance.Should().Be(90m);
        (1000m - buyer.WalletBalance).Should().Be(provider.WalletBalance + escrow.PlatformCommissionAmount);
    }

    [Fact]
    public void RefundToBuyer_AfterCancellation_RestoresBuyerWallet()
    {
        var (booking, escrow, buyer, provider) = TestData.PaidBooking(buyerWallet: 1000m);
        booking.Cancel("Provider double-booked offline");

        EscrowSettlement.RefundToBuyer(booking, escrow, buyer, "Provider double-booked offline");

        booking.Status.Should().Be(BookingStatus.Refunded);
        escrow.Status.Should().Be(EscrowStatus.Refunded);
        buyer.WalletBalance.Should().Be(1000m);
        provider.WalletBalance.Should().Be(0m);
    }

    [Fact]
    public void RefundToBuyer_WrongRecipient_Throws()
    {
        var (booking, escrow, _, provider) = TestData.PaidBooking();
        booking.Cancel("test");

        var act = () => EscrowSettlement.RefundToBuyer(booking, escrow, provider, "test");

        act.Should().Throw<DomainException>().WithMessage("*recipient*");
    }

    [Fact]
    public void Debit_MoreThanBalance_ThrowsInsufficientFunds()
    {
        var buyer = TestData.Buyer(wallet: 20m);

        var act = () => buyer.Debit(45m);

        act.Should().Throw<InsufficientFundsException>();
        buyer.WalletBalance.Should().Be(20m);
    }
}
