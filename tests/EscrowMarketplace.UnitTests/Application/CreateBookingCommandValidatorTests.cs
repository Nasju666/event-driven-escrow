using EscrowMarketplace.Application.Features.Bookings.Commands;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Application;

public class CreateBookingCommandValidatorTests
{
    private readonly CreateBookingCommandValidator _validator = new();

    private static CreateBookingCommand Command(DateTimeOffset start, TimeSpan duration) =>
        new(ResourceId: Guid.NewGuid(), StartTime: start, EndTime: start + duration);

    [Fact]
    public void ValidCommand_Passes()
    {
        var result = _validator.Validate(Command(DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromHours(2)));

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void EndBeforeStart_Fails()
    {
        var result = _validator.Validate(Command(DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromHours(-1)));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBookingCommand.EndTime));
    }

    [Fact]
    public void ShorterThan15Minutes_Fails()
    {
        var result = _validator.Validate(Command(DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromMinutes(10)));

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("at least 15 minutes"));
    }

    [Fact]
    public void LongerThan24Hours_Fails()
    {
        var result = _validator.Validate(Command(DateTimeOffset.UtcNow.AddDays(1), TimeSpan.FromHours(25)));

        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("24 hours"));
    }

    [Fact]
    public void StartInThePast_Fails()
    {
        var result = _validator.Validate(Command(DateTimeOffset.UtcNow.AddHours(-2), TimeSpan.FromHours(1)));

        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateBookingCommand.StartTime));
    }
}
