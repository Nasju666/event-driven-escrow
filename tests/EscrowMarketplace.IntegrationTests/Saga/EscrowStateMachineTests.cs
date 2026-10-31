using EscrowMarketplace.Domain.Events;
using EscrowMarketplace.Infrastructure.Saga;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EscrowMarketplace.IntegrationTests.Saga;

/// <summary>
/// The escrow saga in isolation, on MassTransit's in-memory test harness with the delayed
/// scheduler, so timeouts fire for real (with sub-second deadlines). No database needed.
/// </summary>
public class EscrowStateMachineTests : IAsyncLifetime
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(10);

    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private ISagaStateMachineTestHarness<EscrowStateMachine, EscrowBookingState> _saga = null!;

    private readonly Guid _bookingId = Guid.NewGuid();
    private readonly Guid _tenantId = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.SetTestTimeouts(testInactivityTimeout: TimeSpan.FromSeconds(3));
                x.AddDelayedMessageScheduler();
                x.AddSagaStateMachine<EscrowStateMachine, EscrowBookingState>().InMemoryRepository();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.UseMessageRetry(r => r.Intervals(50, 100, 250, 500)); // same idea as production
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
        _saga = _harness.GetSagaStateMachineHarness<EscrowStateMachine, EscrowBookingState>();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task SlotReserved_StartsWorkflow_AwaitingPayment()
    {
        await Reserve(paymentDueIn: Long);

        (await _saga.Exists(_bookingId, m => m.AwaitingPayment)).Should().NotBeNull();
        (await _harness.Published.Any<EscrowWorkflowTransitioned>(x =>
            x.Context.Message.BookingId == _bookingId && x.Context.Message.State == "AwaitingPayment")).Should().BeTrue();
    }

    [Fact]
    public async Task PaymentWindowElapses_RequestsExpiry_ThenRecordsExpired()
    {
        await Reserve(paymentDueIn: Short);

        (await _harness.Published.Any<ExpireUnpaidBooking>(x => x.Context.Message.BookingId == _bookingId))
            .Should().BeTrue("the saga must ask for the unpaid booking to be expired");

        await Publish(new BookingExpiredEvent(_bookingId, _tenantId, Guid.NewGuid()));
        (await _saga.Exists(_bookingId, m => m.Expired)).Should().NotBeNull();
    }

    [Fact]
    public async Task PaymentBeforeDeadline_CancelsThePaymentTimeout()
    {
        await Reserve(paymentDueIn: Short);
        await Publish(new BookingPaidEvent(_bookingId, _tenantId, DateTimeOffset.UtcNow.Add(Long)));

        (await _saga.Exists(_bookingId, m => m.AwaitingProviderResponse)).Should().NotBeNull();

        await Task.Delay(Short * 3); // the original payment deadline passes
        (await _harness.Published.Any<ExpireUnpaidBooking>(x => x.Context.Message.BookingId == _bookingId))
            .Should().BeFalse("the timeout token was cleared when the buyer paid");
    }

    [Fact]
    public async Task InspectionWindowElapses_RequestsAutomaticRelease()
    {
        await DriveToConfirmed();
        await Publish(new ServiceCompletedEvent(_bookingId, _tenantId, DateTimeOffset.UtcNow.Add(Short)));

        (await _saga.Exists(_bookingId, m => m.InspectionWindow)).Should().NotBeNull();
        (await _harness.Published.Any<ReleaseEscrowAfterInspection>(x => x.Context.Message.BookingId == _bookingId))
            .Should().BeTrue();

        await Publish(new EscrowPayoutReleasedEvent(_bookingId, _tenantId, Guid.NewGuid(), Guid.NewGuid(), 90m, 10m));
        (await _saga.Exists(_bookingId, m => m.Settled)).Should().NotBeNull();
    }

    [Fact]
    public async Task DisputeDuringInspection_CancelsAutomaticRelease()
    {
        await DriveToConfirmed();
        await Publish(new ServiceCompletedEvent(_bookingId, _tenantId, DateTimeOffset.UtcNow.Add(Short)));
        (await _saga.Exists(_bookingId, m => m.InspectionWindow)).Should().NotBeNull();

        await Publish(new EscrowDisputeRaisedEvent(_bookingId, _tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Broken mic"));
        (await _saga.Exists(_bookingId, m => m.Disputed)).Should().NotBeNull();

        await Task.Delay(Short * 3);
        (await _harness.Published.Any<ReleaseEscrowAfterInspection>(x => x.Context.Message.BookingId == _bookingId))
            .Should().BeFalse("a disputed escrow must never be auto-released");

        await Publish(new EscrowRefundedEvent(_bookingId, _tenantId, Guid.NewGuid(), Guid.NewGuid(), 100m, "Resolved for buyer"));
        (await _saga.Exists(_bookingId, m => m.Refunded)).Should().NotBeNull();
    }

    [Fact]
    public async Task PayoutEventArrivingBeforeCompletion_StillSettles()
    {
        // Events from separate transactions can overtake each other in transit.
        // Money-final events are accepted from any state, so the saga can't get stuck.
        await DriveToConfirmed();

        await Publish(new EscrowPayoutReleasedEvent(_bookingId, _tenantId, Guid.NewGuid(), Guid.NewGuid(), 90m, 10m));
        (await _saga.Exists(_bookingId, m => m.Settled)).Should().NotBeNull();

        // The late ServiceCompletedEvent is ignored and schedules nothing.
        await Publish(new ServiceCompletedEvent(_bookingId, _tenantId, DateTimeOffset.UtcNow.Add(Short)));
        await Task.Delay(Short * 3);
        (await _saga.Exists(_bookingId, m => m.Settled)).Should().NotBeNull();
        (await _harness.Published.Any<ReleaseEscrowAfterInspection>(x => x.Context.Message.BookingId == _bookingId))
            .Should().BeFalse();
    }

    private async Task DriveToConfirmed()
    {
        await Reserve(paymentDueIn: Long);
        await Publish(new BookingPaidEvent(_bookingId, _tenantId, DateTimeOffset.UtcNow.Add(Long)));
        (await _saga.Exists(_bookingId, m => m.AwaitingProviderResponse)).Should().NotBeNull();
        await Publish(new ProviderConfirmedBookingEvent(_bookingId, _tenantId, Guid.NewGuid()));
        (await _saga.Exists(_bookingId, m => m.Confirmed)).Should().NotBeNull();
    }

    private async Task Reserve(TimeSpan paymentDueIn)
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        await Publish(new BookingSlotReservedEvent(
            _bookingId, _tenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            start, start.AddHours(1), 100m, "USD", DateTimeOffset.UtcNow.Add(paymentDueIn)));

        (await _saga.Exists(_bookingId, m => m.AwaitingPayment)).Should().NotBeNull();
    }

    private Task Publish<T>(T message) where T : class => _harness.Bus.Publish(message);
}
