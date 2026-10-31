using System.Net;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Enums;
using FluentAssertions;

namespace EscrowMarketplace.IntegrationTests.Api;

/// <summary>Authentication, role checks, ownership checks and tenant isolation.</summary>
[Collection(ApiCollection.Name)]
public class SecurityTests
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/bookings")]
    [InlineData("/api/resources")]
    [InlineData("/api/me")]
    [InlineData("/api/analytics/reconciliation")]
    public async Task Anonymous_IsRejected(string url)
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Provider_CannotCreateBooking()
    {
        var provider = await _factory.ClientForAsync(Users.Provider);
        var (start, end) = ApiExtensions.UniqueSlot();

        var response = await provider.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start, endTime = end });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Buyer_CannotConfirmOwnBooking()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var booking = await buyer.CreateBookingAsync(Resources.LiveRoom);
        await buyer.ActAsync(booking.Id, "pay");

        var response = await buyer.PostAsync($"/api/bookings/{booking.Id}/confirm", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task OtherBuyer_CannotSeeOrPaySomeoneElsesBooking()
    {
        var owner = await _factory.ClientForAsync(Users.Buyer);
        var stranger = await _factory.ClientForAsync(Users.SecondBuyer);
        var booking = await owner.CreateBookingAsync(Resources.LiveRoom);

        (await stranger.GetAsync($"/api/bookings/{booking.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.PostAsync($"/api/bookings/{booking.Id}/pay", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await stranger.GetAsync($"/api/escrows/booking/{booking.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var strangersList = await (await stranger.GetAsync("/api/bookings")).ReadAsync<List<BookingDto>>();
        strangersList.Should().NotContain(b => b.Id == booking.Id);
    }

    [Fact]
    public async Task OtherTenant_CannotSeeBookingsOrResources_EvenWithSpoofedHeader()
    {
        var apexBuyer = await _factory.ClientForAsync(Users.Buyer);
        var northwindBuyer = await _factory.ClientForAsync(Users.OtherTenantBuyer);
        var apexBooking = await apexBuyer.CreateBookingAsync(Resources.LiveRoom);

        // The old API trusted this header, which allowed reading another tenant's data.
        northwindBuyer.DefaultRequestHeaders.Add("X-Tenant-Id", "11111111-1111-1111-1111-111111111111");

        (await northwindBuyer.GetAsync($"/api/bookings/{apexBooking.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var resources = await (await northwindBuyer.GetAsync("/api/resources")).ReadAsync<List<ResourceDto>>();
        resources.Should().ContainSingle().Which.Id.Should().Be(Resources.NorthwindRoom);

        // And booking another tenant's resource is impossible: it does not exist in this tenant.
        var (start, end) = ApiExtensions.UniqueSlot();
        (await northwindBuyer.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start, endTime = end }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OnlyAdmins_CanResolveDisputes_OrSeeAnalytics()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var admin = await _factory.ClientForAsync(Users.Admin);

        (await buyer.PostJsonAsync($"/api/escrows/disputes/{Guid.NewGuid()}/resolve",
            new { resolution = "ResolvedInFavorOfBuyer", resolutionNotes = "x" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.GetAsync("/api/analytics/reconciliation")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.GetAsync("/api/analytics/reconciliation")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PayingMoreThanWalletBalance_Returns422_AndNothingChanges()
    {
        var buyer = await _factory.ClientForAsync(Users.SecondBuyer);
        var before = await buyer.WalletAsync();

        // 12h in the mixing suite = $1,440, more than the seeded $800 wallet.
        var booking = await buyer.CreateBookingAsync(Resources.MixingSuite, hours: 12);
        var response = await buyer.PostAsync($"/api/bookings/{booking.Id}/pay", null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await buyer.WalletAsync()).Should().Be(before);
        (await buyer.GetBookingAsync(booking.Id)).Status.Should().Be(BookingStatus.PendingPayment);
    }
}
