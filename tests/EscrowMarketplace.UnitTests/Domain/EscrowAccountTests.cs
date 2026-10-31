using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

public class EscrowAccountTests
{
    private static EscrowAccount NewEscrow(decimal total = 100m, decimal commission = 10m) =>
        EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), total, commission);

    private static EscrowAccount FundedEscrow(decimal total = 100m)
    {
        var escrow = NewEscrow(total);
        escrow.Authorize("ref_123");
        escrow.Capture();
        return escrow;
    }

    [Fact]
    public void Create_CalculatesCommissionAndProviderPayout()
    {
        var escrow = EscrowAccount.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 250.00m, 12.5m);

        escrow.PlatformCommissionAmount.Should().Be(31.25m);
        escrow.ProviderPayoutAmount.Should().Be(218.75m);
        (escrow.PlatformCommissionAmount + escrow.ProviderPayoutAmount).Should().Be(escrow.TotalAmount);
        escrow.Status.Should().Be(EscrowStatus.PendingAuthorization);
    }

    [Fact]
    public void Capture_RecordsHoldDeposit_AndRaisesEvent()
    {
        var escrow = FundedEscrow(100m);

        escrow.Status.Should().Be(EscrowStatus.Funded);
        escrow.FundedAt.Should().NotBeNull();
        escrow.LedgerEntries.Should().ContainSingle(l => l.EntryType == LedgerEntryType.HoldDeposit && l.Amount == 100m);
        escrow.DomainEvents.Should().Contain(e => e is EscrowFundsCapturedEvent);
    }

    [Fact]
    public void Capture_WithoutAuthorization_Throws()
    {
        var act = () => NewEscrow().Capture();

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void LedgerEntries_HaveNoPresetKey_SoEfInsertsThem()
    {
        // A preset Guid on an entity reached through a navigation makes EF treat it as an
        // existing row (UPDATE instead of INSERT). EF assigns the key when the entry is tracked.
        FundedEscrow().LedgerEntries.Should().OnlyContain(l => l.Id == Guid.Empty);
    }

    [Fact]
    public void ReleasePayout_RecordsPayoutAndFee()
    {
        var escrow = FundedEscrow(100m);

        escrow.ReleasePayout();

        escrow.Status.Should().Be(EscrowStatus.Settled);
        escrow.LedgerEntries.Should().HaveCount(3);
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.ProviderPayout && l.Amount == 90m);
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.PlatformFee && l.Amount == 10m);
        escrow.DomainEvents.Should().Contain(e => e is EscrowPayoutReleasedEvent);
    }

    [Fact]
    public void ReleasePayout_FromDisputed_Succeeds()
    {
        // Regression: resolving a dispute in the provider's favour always threw,
        // because ReleasePayout only accepted Funded.
        var escrow = FundedEscrow();
        escrow.MarkDisputed();

        escrow.ReleasePayout();

        escrow.Status.Should().Be(EscrowStatus.Settled);
    }

    [Fact]
    public void Refund_FromFundedOrDisputed_RecordsFullRefund()
    {
        var escrow = FundedEscrow(100m);
        escrow.MarkDisputed();

        escrow.Refund("Equipment failure");

        escrow.Status.Should().Be(EscrowStatus.Refunded);
        escrow.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.RefundToBuyer && l.Amount == 100m);
        escrow.DomainEvents.Should().Contain(e => e is EscrowRefundedEvent);
    }

    [Fact]
    public void ReleasePayout_WhenNotFunded_Throws()
    {
        var act = () => NewEscrow().ReleasePayout();

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void Void_OnlyBeforeMoneyMoved()
    {
        var unpaid = NewEscrow();
        unpaid.Void();
        unpaid.Status.Should().Be(EscrowStatus.Voided);
        unpaid.LedgerEntries.Should().BeEmpty();

        var act = () => FundedEscrow().Void();
        act.Should().Throw<InvalidStateTransitionException>();
    }
}
