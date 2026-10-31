using EscrowMarketplace.Application.Common.Interfaces;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Enums;
using FluentAssertions;

namespace EscrowMarketplace.IntegrationTests.Api;

/// <summary>
/// End-to-end escrow lifecycle through the HTTP API: wallet movements, the ledger, disputes,
/// and the saga's inspection-window timeout (3 seconds in appsettings.Testing.json), running on
/// PostgreSQL + EF outbox + saga + delayed scheduler.
/// </summary>
[Collection(ApiCollection.Name)]
public class EscrowWorkflowTests
{
    private static readonly TimeSpan SagaTimeout = TimeSpan.FromSeconds(30);
    private readonly CustomWebApplicationFactory _factory;

    public EscrowWorkflowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BuyerApprovesEarly_PayoutReleased_AndMoneyBalances()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);
        var buyerBefore = await buyer.WalletAsync();
        var providerBefore = await provider.WalletAsync();

        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);   // $45
        (await buyer.ActAsync(booking.Id, "pay")).Status.Should().Be(BookingStatus.Held);
        (await buyer.WalletAsync()).Should().Be(buyerBefore - 45m);

        (await provider.ActAsync(booking.Id, "confirm")).Status.Should().Be(BookingStatus.Confirmed);
        var completed = await provider.ActAsync(booking.Id, "complete");
        completed.Status.Should().Be(BookingStatus.Completed);
        completed.InspectionDeadlineUtc.Should().NotBeNull();

        var settled = await buyer.ActAsync(booking.Id, "approve");

        settled.Status.Should().Be(BookingStatus.Settled);
        settled.EscrowStatus.Should().Be(EscrowStatus.Settled);
        (await provider.WalletAsync()).Should().Be(providerBefore + 40.50m);  // 45 - 10% fee

        var escrow = await (await buyer.GetAsync($"/api/escrows/booking/{booking.Id}")).ReadAsync<EscrowDto>();
        escrow.LedgerEntries.Select(l => l.EntryType).Should().BeEquivalentTo(new[]
        {
            LedgerEntryType.HoldDeposit, LedgerEntryType.ProviderPayout, LedgerEntryType.PlatformFee
        });
    }

    [Fact]
    public async Task InspectionWindowElapses_SagaReleasesPayoutAutomatically()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);
        var providerBefore = await provider.WalletAsync();

        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        await buyer.ActAsync(booking.Id, "pay");
        await provider.ActAsync(booking.Id, "confirm");
        await provider.ActAsync(booking.Id, "complete");

        // Nobody calls approve. The ServiceCompletedEvent goes through the outbox to the saga,
        // which schedules InspectionWindowElapsed. When it fires, the saga issues
        // ReleaseEscrowAfterInspection and the consumer settles the booking as the system actor.
        var settled = await buyer.WaitForStatusAsync(booking.Id, BookingStatus.Settled, SagaTimeout);

        settled.EscrowStatus.Should().Be(EscrowStatus.Settled);
        (await provider.WalletAsync()).Should().Be(providerBefore + 40.50m);

        var workflow = await WaitForWorkflowStateAsync(buyer, booking.Id, "Settled");
        workflow.NextDeadlineUtc.Should().BeNull();
    }

    [Fact]
    public async Task Dispute_FreezesFunds_AndBlocksAutoRelease()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);

        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        await buyer.ActAsync(booking.Id, "pay");
        await provider.ActAsync(booking.Id, "confirm");
        await provider.ActAsync(booking.Id, "complete");
        (await buyer.PostJsonAsync($"/api/bookings/{booking.Id}/dispute",
            new { reason = "Booth mic was broken", evidenceSummary = "Recording attached" })).EnsureSuccessStatusCode();

        // Well past the 3s inspection window: the scheduled timeout must not release anything.
        await Task.Delay(TimeSpan.FromSeconds(6));

        var current = await buyer.GetBookingAsync(booking.Id);
        current.Status.Should().Be(BookingStatus.Disputed);
        current.EscrowStatus.Should().Be(EscrowStatus.Disputed);
        current.OpenDisputeId.Should().NotBeNull();
    }

    [Fact]
    public async Task DisputeResolvedForProvider_ReleasesPayout()
    {
        // Regression test: this path used to always throw (ReleasePayout required Funded).
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);
        var admin = await _factory.ClientForAsync(Users.Admin);
        var providerBefore = await provider.WalletAsync();

        var disputeId = await CreateDisputedBookingAsync(buyer, provider);

        var resolved = await (await admin.PostJsonAsync($"/api/escrows/disputes/{disputeId}/resolve", new
        {
            resolution = DisputeStatus.ResolvedInFavorOfProvider,
            resolutionNotes = "Session logs show the booth was working."
        })).ReadAsync<DisputeDto>();

        resolved.Status.Should().Be(DisputeStatus.ResolvedInFavorOfProvider);
        (await buyer.GetBookingAsync(resolved.BookingId)).Status.Should().Be(BookingStatus.Settled);
        (await provider.WalletAsync()).Should().Be(providerBefore + 40.50m);
    }

    [Fact]
    public async Task DisputeResolvedForBuyer_RefundsInFull()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);
        var admin = await _factory.ClientForAsync(Users.Admin);
        var buyerBefore = await buyer.WalletAsync();

        var disputeId = await CreateDisputedBookingAsync(buyer, provider);

        var resolved = await (await admin.PostJsonAsync($"/api/escrows/disputes/{disputeId}/resolve", new
        {
            resolution = DisputeStatus.ResolvedInFavorOfBuyer,
            resolutionNotes = "Provider confirmed the mic failed."
        })).ReadAsync<DisputeDto>();

        var booking = await buyer.GetBookingAsync(resolved.BookingId);
        booking.Status.Should().Be(BookingStatus.Refunded);
        booking.EscrowStatus.Should().Be(EscrowStatus.Refunded);
        (await buyer.WalletAsync()).Should().Be(buyerBefore);   // paid 45, got 45 back
    }

    [Fact]
    public async Task ProviderCancelsAfterPayment_BuyerRefundedInFull()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var provider = await _factory.ClientForAsync(Users.Provider);
        var buyerBefore = await buyer.WalletAsync();

        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        await buyer.ActAsync(booking.Id, "pay");

        var cancelled = await provider.ActAsync(booking.Id, "cancel", new { reason = "Studio flooded" });

        cancelled.Status.Should().Be(BookingStatus.Refunded);
        cancelled.EscrowStatus.Should().Be(EscrowStatus.Refunded);
        (await buyer.WalletAsync()).Should().Be(buyerBefore);
    }

    [Fact]
    public async Task CancelBeforePayment_VoidsEscrow_NoMoneyMoves()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var buyerBefore = await buyer.WalletAsync();

        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        var cancelled = await buyer.ActAsync(booking.Id, "cancel", new { reason = "Booked by mistake" });

        cancelled.Status.Should().Be(BookingStatus.Cancelled);
        cancelled.EscrowStatus.Should().Be(EscrowStatus.Voided);
        (await buyer.WalletAsync()).Should().Be(buyerBefore);
    }

    private static async Task<Guid> CreateDisputedBookingAsync(HttpClient buyer, HttpClient provider)
    {
        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        await buyer.ActAsync(booking.Id, "pay");
        await provider.ActAsync(booking.Id, "confirm");

        // Dispute before completion, so the 3s inspection timeout plays no part in this test.
        var dispute = await (await buyer.PostJsonAsync($"/api/bookings/{booking.Id}/dispute",
            new { reason = "Studio double-booked on arrival", evidenceSummary = "Photos" })).ReadAsync<DisputeDto>();
        return dispute.Id;
    }

    private static async Task<WorkflowStateDto> WaitForWorkflowStateAsync(HttpClient client, Guid bookingId, string state)
    {
        var deadline = DateTime.UtcNow + SagaTimeout;
        WorkflowStateDto? last = null;
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync($"/api/bookings/{bookingId}/workflow");
            if (response.IsSuccessStatusCode)
            {
                last = await response.ReadAsync<WorkflowStateDto>();
                if (last.State == state) return last;
            }
            await Task.Delay(250);
        }

        throw new TimeoutException($"Workflow for {bookingId} is '{last?.State ?? "missing"}', expected '{state}'.");
    }
}
