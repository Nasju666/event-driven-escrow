using System.Net;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;

namespace EscrowMarketplace.IntegrationTests.Api;

/// <summary>Double-booking prevention against real PostgreSQL.</summary>
[Collection(ApiCollection.Name)]
public class BookingEndpointsTests
{
    private readonly CustomWebApplicationFactory _factory;

    public BookingEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_ReturnsPendingPayment_WithEscrowSummary()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);

        var booking = await buyer.CreateBookingAsync(Resources.LiveRoom, hours: 2);

        booking.Status.Should().Be(BookingStatus.PendingPayment);
        booking.TotalAmount.Should().Be(150m);                 // 2h × $75
        booking.EscrowStatus.Should().Be(EscrowStatus.PendingAuthorization);
        booking.PlatformCommissionAmount.Should().Be(15m);     // 10% tenant commission
        booking.ProviderPayoutAmount.Should().Be(135m);
        booking.PaymentDueUtc.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OverlappingSlot_Returns409()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var (start, end) = ApiExtensions.UniqueSlot(hours: 2);

        var first = await buyer.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start, endTime = end });
        var overlap = await buyer.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start.AddHours(1), endTime = end.AddHours(1) });

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        overlap.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await overlap.Content.ReadFromJsonAsync<ProblemDetails>())!.Status.Should().Be(409);
    }

    [Fact]
    public async Task AdjacentSlots_DoNotConflict_BecauseRangesAreHalfOpen()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var (start, _) = ApiExtensions.UniqueSlot();

        var a = await buyer.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start, endTime = start.AddHours(1) });
        var b = await buyer.PostJsonAsync("/api/bookings", new { resourceId = Resources.LiveRoom, startTime = start.AddHours(1), endTime = start.AddHours(2) });

        a.StatusCode.Should().Be(HttpStatusCode.Created);
        b.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task ConcurrentOverlappingRequests_ExactlyOneWins()
    {
        // Twelve buyers hit the same slot at once. All of them can pass the application-level
        // pre-check before any of them commits, so this only holds because PostgreSQL's
        // GiST exclusion constraint rejects every insert after the first (SQLSTATE 23P01 -> 409).
        var buyerA = await _factory.ClientForAsync(Users.Buyer);
        var buyerB = await _factory.ClientForAsync(Users.SecondBuyer);
        var (start, end) = ApiExtensions.UniqueSlot(hours: 1);

        var requests = Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? buyerA : buyerB).PostJsonAsync("/api/bookings",
                new { resourceId = Resources.MixingSuite, startTime = start, endTime = end }));

        var responses = await Task.WhenAll(requests);

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(11);
    }

    [Fact]
    public async Task CancelledBooking_FreesTheSlot()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var (start, end) = ApiExtensions.UniqueSlot();

        var first = await (await buyer.PostJsonAsync("/api/bookings",
            new { resourceId = Resources.VocalBooth, startTime = start, endTime = end })).ReadAsync<BookingDto>();
        await buyer.ActAsync(first.Id, "cancel", new { reason = "Plans changed" });

        var again = await buyer.PostJsonAsync("/api/bookings",
            new { resourceId = Resources.VocalBooth, startTime = start, endTime = end });

        again.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AvailableSlots_MarksBookedIntervals()
    {
        var buyer = await _factory.ClientForAsync(Users.Buyer);
        var booking = await buyer.CreateBookingAsync(Resources.VocalBooth);
        var day = DateOnly.FromDateTime(booking.StartTime.UtcDateTime);

        var slots = await (await buyer.GetAsync(
            $"/api/bookings/available-slots?resourceId={Resources.VocalBooth}&targetDate={day:yyyy-MM-dd}&durationMinutes=60"))
            .ReadAsync<List<TimeSlotDto>>();

        slots.Should().Contain(s => s.StartTime == booking.StartTime && !s.IsAvailable);
        slots.Should().Contain(s => s.IsAvailable);
    }
}
