using EscrowMarketplace.Application.Features.Bookings.Commands;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Application;

public class CreateBookingCommandValidatorTests
{
    private readonly CreateBookingCommandValidator _validator = new();

    [Fact]
    public void Validate_ValidCommand_Passes()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var command = new CreateBookingCommand(
            ResourceId: Guid.NewGuid(),
            BuyerId: Guid.NewGuid(),
            StartTime: start,
            EndTime: start.AddHours(2));

        var result = _validator.Validate(command);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EndTimeBeforeStartTime_Fails()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var command = new CreateBookingCommand(
            ResourceId: Guid.NewGuid(),
            BuyerId: Guid.NewGuid(),
            StartTime: start,
            EndTime: start.AddHours(-1));

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBookingCommand.EndTime));
    }

    [Fact]
    public void Validate_DurationLessThan15Minutes_Fails()
    {
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var command = new CreateBookingCommand(
            ResourceId: Guid.NewGuid(),
            BuyerId: Guid.NewGuid(),
            StartTime: start,
            EndTime: start.AddMinutes(10));

        var result = _validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("at least 15 minutes"));
    }
}
