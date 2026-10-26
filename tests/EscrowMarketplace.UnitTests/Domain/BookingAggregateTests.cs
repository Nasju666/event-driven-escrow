using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Exceptions;
using EscrowMarketplace.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

public class BookingAggregateTests
{
    [Fact]
    public void ValidBookingLifecycle_TransitionsSuccessfully()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var booking = new Booking
        {
            TenantId = Guid.NewGuid(),
            ResourceId = Guid.NewGuid(),
            BuyerId = Guid.NewGuid(),
            ProviderId = Guid.NewGuid(),
            Period = new TimeRange(start, start.AddHours(2)),
            TotalAmount = 150m,
            Status = BookingStatus.PendingPayment
        };

        booking.Status.Should().Be(BookingStatus.PendingPayment);

        booking.MarkHeld();
        booking.Status.Should().Be(BookingStatus.Held);

        booking.Confirm();
        booking.Status.Should().Be(BookingStatus.Confirmed);

        booking.StartService();
        booking.Status.Should().Be(BookingStatus.InService);

        booking.Complete();
        booking.Status.Should().Be(BookingStatus.Completed);
    }

    [Fact]
    public void InvalidStateTransition_ThrowsInvalidStateTransitionException()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var booking = new Booking
        {
            Period = new TimeRange(start, start.AddHours(2)),
            Status = BookingStatus.PendingPayment
        };

        // Attempting to complete a booking directly from PendingPayment must fail
        var act = () => booking.Complete();

        act.Should().Throw<InvalidStateTransitionException>()
            .WithMessage("*Invalid state transition for Booking*");
    }

    [Fact]
    public void CancelCompletedBooking_ThrowsInvalidStateTransitionException()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var booking = new Booking
        {
            Period = new TimeRange(start, start.AddHours(2)),
            Status = BookingStatus.Completed
        };

        var act = () => booking.Cancel();

        act.Should().Throw<InvalidStateTransitionException>();
    }
}
