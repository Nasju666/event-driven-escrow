using EscrowMarketplace.Domain.Entities;
using EscrowMarketplace.Domain.Enums;
using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

public class BookingAggregateTests
{
    [Fact]
    public void Reserve_CalculatesTotal_StartsPendingPayment_AndRaisesSlotReservedEvent()
    {
        var (booking, _, buyer, provider) = TestData.ReservedBooking(hourlyRate: 75m, hours: 2);

        booking.Status.Should().Be(BookingStatus.PendingPayment);
        booking.TotalAmount.Should().Be(150m);
        booking.BuyerId.Should().Be(buyer.Id);
        booking.ProviderId.Should().Be(provider.Id);

        var evt = booking.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<BookingSlotReservedEvent>().Subject;
        evt.BookingId.Should().Be(booking.Id);
        evt.PaymentDueUtc.Should().Be(booking.PaymentDueUtc);
    }

    [Fact]
    public void Reserve_ProviderBookingOwnResource_Throws()
    {
        var provider = TestData.Provider();
        var resource = TestData.Resource(provider.Id);

        var act = () => Booking.Reserve(TestData.TenantId, resource, provider.Id, TestData.Period(), DateTimeOffset.UtcNow.AddMinutes(15));

        act.Should().Throw<DomainException>().WithMessage("*own resources*");
    }

    [Fact]
    public void HappyPath_RaisesOneEventPerTransition()
    {
        var (booking, _, _, _) = TestData.ReservedBooking();
        booking.ClearDomainEvents();

        booking.MarkPaid(DateTimeOffset.UtcNow.AddHours(1));
        booking.Confirm();
        booking.Complete(DateTimeOffset.UtcNow.AddDays(3));
        booking.MarkSettled();

        booking.Status.Should().Be(BookingStatus.Settled);
        booking.DomainEvents.Select(e => e.GetType()).Should().Equal(
            typeof(BookingPaidEvent),
            typeof(ProviderConfirmedBookingEvent),
            typeof(ServiceCompletedEvent));
    }

    [Fact]
    public void Complete_BeforeProviderConfirms_Throws()
    {
        var (booking, _, _, _) = TestData.ReservedBooking();
        booking.MarkPaid(DateTimeOffset.UtcNow.AddHours(1));

        var act = () => booking.Complete(DateTimeOffset.UtcNow.AddDays(3));

        act.Should().Throw<InvalidStateTransitionException>().WithMessage("*Held*Completed*");
    }

    [Fact]
    public void Confirm_BeforePayment_Throws()
    {
        var (booking, _, _, _) = TestData.ReservedBooking();

        var act = () => booking.Confirm();

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Theory]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Disputed)]   // regression: cancelling a disputed booking used to strand the escrow
    [InlineData(BookingStatus.Settled)]
    public void Cancel_AfterCompletionOrDispute_Throws(BookingStatus status)
    {
        var (booking, _, _, _) = TestData.ReservedBooking();
        booking.Status = status;

        var act = () => booking.Cancel("changed my mind");

        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void Expire_OnlyAllowedWhileUnpaid()
    {
        var (unpaid, _, _, _) = TestData.ReservedBooking();
        unpaid.Expire();
        unpaid.Status.Should().Be(BookingStatus.Expired);
        unpaid.DomainEvents.Should().ContainSingle(e => e is BookingExpiredEvent);

        var (paid, _, _, _) = TestData.PaidBooking();
        var act = () => paid.Expire();
        act.Should().Throw<InvalidStateTransitionException>();
    }

    [Fact]
    public void MarkDisputed_BeforeProviderConfirms_Throws()
    {
        var (booking, _, _, _) = TestData.PaidBooking();

        var act = () => booking.MarkDisputed();

        act.Should().Throw<InvalidStateTransitionException>();
    }
}
