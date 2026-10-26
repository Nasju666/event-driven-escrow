using System.Net;
using System.Net.Http.Json;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Domain.Enums;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.IntegrationTests.Api;

public class EscrowWorkflowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _resourceId = Guid.Parse("66666666-6666-6666-6666-666666666666"); // $120/hr
    private readonly Guid _providerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly Guid _buyerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public EscrowWorkflowTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId.ToString());
    }

    [Fact]
    public async Task CompleteEscrowLifecycle_Authorize_Fund_Release_RecordsLedgerEntries()
    {
        // 1. Create Booking (2 hours = $240, 10% commission = $24 platform fee, $216 provider payout)
        var start = DateTimeOffset.UtcNow.AddDays(15).Date.AddHours(14);
        var createCmd = new CreateBookingCommand(_resourceId, _buyerId, start, start.AddHours(2));
        var bookingResp = await _client.PostAsJsonAsync("/api/bookings", createCmd);
        var booking = await bookingResp.Content.ReadFromJsonAsync<BookingDto>();

        // 2. Fetch Escrow
        var escrowResp = await _client.GetAsync($"/api/escrows/booking/{booking!.Id}");
        escrowResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var escrow = await escrowResp.Content.ReadFromJsonAsync<EscrowDto>();
        escrow!.Status.Should().Be(EscrowStatus.PendingAuthorization);
        escrow.TotalAmount.Should().Be(240.00m);
        escrow.PlatformCommissionAmount.Should().Be(24.00m);
        escrow.ProviderPayoutAmount.Should().Be(216.00m);

        // 3. Authorize Escrow
        var authResp = await _client.PostAsJsonAsync($"/api/escrows/{escrow.Id}/authorize", new { PaymentReference = "mock_stripe_ch_998877" });
        authResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var authorized = await authResp.Content.ReadFromJsonAsync<EscrowDto>();
        authorized!.Status.Should().Be(EscrowStatus.Authorized);

        // 4. Fund Escrow (Capture funds into segregated hold ledger)
        var fundResp = await _client.PostAsync($"/api/escrows/{escrow.Id}/fund?inspectionDays=2", null);
        fundResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var funded = await fundResp.Content.ReadFromJsonAsync<EscrowDto>();
        funded!.Status.Should().Be(EscrowStatus.Funded);
        funded.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.HoldDeposit && l.Amount == 240.00m);

        // 5. Release Escrow Payout (Service verified -> credit provider $216, capture platform fee $24)
        var releaseResp = await _client.PostAsync($"/api/escrows/{escrow.Id}/release", null);
        releaseResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var settled = await releaseResp.Content.ReadFromJsonAsync<EscrowDto>();
        settled!.Status.Should().Be(EscrowStatus.Settled);
        settled.LedgerEntries.Should().HaveCount(3);
        settled.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.ProviderPayout && l.Amount == 216.00m);
        settled.LedgerEntries.Should().Contain(l => l.EntryType == LedgerEntryType.PlatformFee && l.Amount == 24.00m);
    }

    [Fact]
    public async Task DisputeWorkflow_CanRaiseAndResolveDispute()
    {
        var start = DateTimeOffset.UtcNow.AddDays(20).Date.AddHours(10);
        var createCmd = new CreateBookingCommand(_resourceId, _buyerId, start, start.AddHours(1));
        var bookingResp = await _client.PostAsJsonAsync("/api/bookings", createCmd);
        var booking = await bookingResp.Content.ReadFromJsonAsync<BookingDto>();

        var escrowResp = await _client.GetAsync($"/api/escrows/booking/{booking!.Id}");
        var escrow = await escrowResp.Content.ReadFromJsonAsync<EscrowDto>();

        // Fund escrow
        await _client.PostAsJsonAsync($"/api/escrows/{escrow!.Id}/authorize", new { PaymentReference = "ch_dispute_123" });
        await _client.PostAsync($"/api/escrows/{escrow.Id}/fund", null);

        // Raise Dispute
        var disputeReq = new
        {
            RaisedByUserId = _buyerId,
            Reason = "Equipment in studio was malfunctioning during booked session",
            EvidenceSummary = "Mixing console power failure audio drop logs attached"
        };

        var disputeResp = await _client.PostAsJsonAsync($"/api/escrows/{escrow.Id}/dispute", disputeReq);
        disputeResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dispute = await disputeResp.Content.ReadFromJsonAsync<DisputeDto>();
        dispute!.Status.Should().Be(DisputeStatus.PendingReview);

        // Resolve in favor of Buyer (Refund)
        var resolveReq = new
        {
            Resolution = DisputeStatus.ResolvedInFavorOfBuyer,
            ResolutionNotes = "Verified equipment malfunction with provider host. Full buyer refund issued."
        };

        var resolveResp = await _client.PostAsJsonAsync($"/api/escrows/disputes/{dispute.Id}/resolve", resolveReq);
        resolveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var resolved = await resolveResp.Content.ReadFromJsonAsync<DisputeDto>();
        resolved!.Status.Should().Be(DisputeStatus.ResolvedInFavorOfBuyer);
    }
}
