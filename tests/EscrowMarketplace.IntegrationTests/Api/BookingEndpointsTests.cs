using System.Net;
using System.Net.Http.Json;
using EscrowMarketplace.Application.Common.Models;
using EscrowMarketplace.Application.Features.Bookings.Commands;
using EscrowMarketplace.Domain.Enums;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace EscrowMarketplace.IntegrationTests.Api;

public class BookingEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly Guid _tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly Guid _resourceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private readonly Guid _providerId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private readonly Guid _buyerId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public BookingEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Tenant-Id", _tenantId.ToString());
    }

    [Fact]
    public async Task CreateBooking_ValidSlot_Returns201Created()
    {
        var start = DateTimeOffset.UtcNow.AddDays(5).Date.AddHours(10);
        var end = start.AddHours(2);

        var command = new CreateBookingCommand(
            ResourceId: _resourceId,
            BuyerId: _buyerId,
            StartTime: start,
            EndTime: end);

        var response = await _client.PostAsJsonAsync("/api/bookings", command);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<BookingDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be(BookingStatus.Held);
        created.TotalAmount.Should().Be(150.00m); // 2 hours * $75
    }

    [Fact]
    public async Task CreateBooking_OverlappingSlot_Returns409Conflict()
    {
        // First reservation: [14:00, 16:00)
        var start = DateTimeOffset.UtcNow.AddDays(6).Date.AddHours(14);
        var end = start.AddHours(2);

        var firstBooking = new CreateBookingCommand(_resourceId, _buyerId, start, end);
        var response1 = await _client.PostAsJsonAsync("/api/bookings", firstBooking);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);

        // Second overlapping reservation: [15:00, 17:00)
        var overlappingBooking = new CreateBookingCommand(_resourceId, _buyerId, start.AddHours(1), end.AddHours(1));
        var response2 = await _client.PostAsJsonAsync("/api/bookings", overlappingBooking);

        // Must return 409 Conflict with Problem Details!
        response2.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response2.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be((int)HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConfirmBooking_ByProvider_Returns200WithConfirmedStatus()
    {
        var start = DateTimeOffset.UtcNow.AddDays(7).Date.AddHours(9);
        var command = new CreateBookingCommand(_resourceId, _buyerId, start, start.AddHours(1));

        var createResp = await _client.PostAsJsonAsync("/api/bookings", command);
        var booking = await createResp.Content.ReadFromJsonAsync<BookingDto>();

        var confirmResp = await _client.PostAsJsonAsync($"/api/bookings/{booking!.Id}/confirm", new { ProviderId = _providerId });

        confirmResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var confirmed = await confirmResp.Content.ReadFromJsonAsync<BookingDto>();
        confirmed!.Status.Should().Be(BookingStatus.Confirmed);
    }

    [Fact]
    public async Task GetAvailableSlots_ReturnsAvailableAndBookedIntervals()
    {
        var targetDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10));
        var dateStart = targetDate.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc);

        // Book [10:00, 12:00)
        var command = new CreateBookingCommand(_resourceId, _buyerId, dateStart, dateStart.AddHours(2));
        await _client.PostAsJsonAsync("/api/bookings", command);

        var response = await _client.GetAsync($"/api/bookings/available-slots?resourceId={_resourceId}&targetDate={targetDate:yyyy-MM-dd}&durationMinutes=60");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var slots = await response.Content.ReadFromJsonAsync<IReadOnlyList<TimeSlotDto>>();
        slots.Should().NotBeNull();
        slots.Should().NotBeEmpty();

        // 10:00 to 11:00 slot must be unavailable
        slots!.First(s => s.StartTime == dateStart).IsAvailable.Should().BeFalse();
    }
}
