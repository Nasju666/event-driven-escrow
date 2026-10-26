using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

public class EscrowAccountTests
{
    [Fact]
    public void Create_CalculatesCommissionAndProviderPayoutAccurately()
    {
        var tenantId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        var providerId = Guid.NewGuid();

        // $250 total booking amount with 12.5% platform commission
        var escrow = EscrowAccount.Create(tenantId, bookingId, buyerId, providerId, 250.00m, 12.5m, "USD");

        escrow.TotalAmount.Should().Be(250.00m);
        escrow.PlatformCommissionPercent.Should().Be(12.5m);
        escrow.PlatformCommissionAmount.Should().Be(31.25m); // 250 * 0.125
        escrow.ProviderPayoutAmount.Should().Be(218.75m);    // 250 - 31.25
        escrow.Status.Should().Be(EscrowStatus.PendingAuthorization);
    }

    [Fact]
    public void MarkFunded_CreatesCreditLedgerEntryAndSetsInspectionDeadline()
    {
        var escrow = EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, 10m);
        escrow.MarkAuthorized("auth_12345");

        var deadline = DateTimeOffset.UtcNow.AddDays(3);
        escrow.MarkFunded(deadline);

        escrow.Status.Should().Be(EscrowStatus.Funded);
        escrow.InspectionDeadlineUtc.Should().Be(deadline);
        escrow.FundedAt.Should().NotBeNull();

        escrow.LedgerEntries.Should().HaveCount(1);
        var entry = escrow.LedgerEntries.First();
        entry.EntryType.Should().Be(LedgerEntryType.HoldDeposit);
        entry.Amount.Should().Be(100m);
    }

    [Fact]
    public void ReleasePayout_CreatesDisbursementAndCommissionLedgerEntries()
    {
        var escrow = EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, 10m);
        escrow.MarkAuthorized("auth_12345");
        escrow.MarkFunded(DateTimeOffset.UtcNow.AddDays(3));

        escrow.ReleasePayout();

        escrow.Status.Should().Be(EscrowStatus.Settled);
        escrow.SettledAt.Should().NotBeNull();

        // 1 deposit + 1 provider payout + 1 platform fee = 3 ledger records
        escrow.LedgerEntries.Should().HaveCount(3);
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.ProviderPayout && l.Amount == 90m);
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.PlatformFee && l.Amount == 10m);
    }

    [Fact]
    public void Refund_CreatesBuyerRefundLedgerEntry()
    {
        var escrow = EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, 10m);
        escrow.MarkAuthorized("auth_12345");
        escrow.MarkFunded(DateTimeOffset.UtcNow.AddDays(3));

        escrow.Refund("Provider cancelled");

        escrow.Status.Should().Be(EscrowStatus.Refunded);
        escrow.RefundedAt.Should().NotBeNull();
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.RefundToBuyer && l.Amount == 100m);
    }

    [Fact]
    public void ReleasePayout_WhenNotFunded_ThrowsInvalidStateTransitionException()
    {
        var escrow = EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100m, 10m);

        var act = () => escrow.ReleasePayout();

        act.Should().Throw<InvalidStateTransitionException>();
    }
}
