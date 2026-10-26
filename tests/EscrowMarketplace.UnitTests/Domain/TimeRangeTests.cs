using EscrowMarketplace.Domain.ValueObjects;
using FluentAssertions;
using Xunit;

namespace EscrowMarketplace.UnitTests.Domain;

public class TimeRangeTests
{
    [Fact]
    public void Constructor_WithValidUtcRange_InitializesCorrectly()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(2);

        var range = new TimeRange(start, end);

        range.Start.Should().Be(start.ToUniversalTime());
        range.End.Should().Be(end.ToUniversalTime());
        range.Duration.Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Constructor_WithEndBeforeOrEqualStart_ThrowsArgumentException()
    {
        var start = DateTimeOffset.UtcNow;
        var end = start.AddHours(-1);

        var act = () => new TimeRange(start, end);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*must be strictly greater than start time*");
    }

    [Fact]
    public void Overlaps_WithStrictlyOverlappingWindows_ReturnsTrue()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var range1 = new TimeRange(baseTime, baseTime.AddHours(2));            // [0, 2)
        var range2 = new TimeRange(baseTime.AddHours(1), baseTime.AddHours(3)); // [1, 3)

        range1.Overlaps(range2).Should().BeTrue();
        range2.Overlaps(range1).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_WithAdjacentWindows_ReturnsFalse()
    {
        var baseTime = DateTimeOffset.UtcNow;
        // Half-open interval semantics: [09:00, 11:00) and [11:00, 13:00) share no common point!
        var range1 = new TimeRange(baseTime, baseTime.AddHours(2));
        var range2 = new TimeRange(baseTime.AddHours(2), baseTime.AddHours(4));

        range1.Overlaps(range2).Should().BeFalse();
        range2.Overlaps(range1).Should().BeFalse();
        range1.IsAdjacentTo(range2).Should().BeTrue();
    }

    [Fact]
    public void Overlaps_WithCompletelyDisjointWindows_ReturnsFalse()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var range1 = new TimeRange(baseTime, baseTime.AddHours(1));
        var range2 = new TimeRange(baseTime.AddHours(3), baseTime.AddHours(5));

        range1.Overlaps(range2).Should().BeFalse();
        range1.IsAdjacentTo(range2).Should().BeFalse();
    }

    [Fact]
    public void Contains_TimestampWithinWindow_ReturnsTrue()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var range = new TimeRange(baseTime, baseTime.AddHours(2));

        range.Contains(baseTime).Should().BeTrue(); // Inclusive lower bound
        range.Contains(baseTime.AddHours(1)).Should().BeTrue();
        range.Contains(baseTime.AddHours(2)).Should().BeFalse(); // Exclusive upper bound
    }

    [Fact]
    public void Intersect_OverlappingRanges_ReturnsExactOverlapInterval()
    {
        var baseTime = DateTimeOffset.UtcNow;
        var range1 = new TimeRange(baseTime, baseTime.AddHours(3));             // [0, 3)
        var range2 = new TimeRange(baseTime.AddHours(1), baseTime.AddHours(4)); // [1, 4)

        var intersection = range1.Intersect(range2);

        intersection.Should().NotBeNull();
        intersection!.Start.Should().Be(baseTime.AddHours(1).ToUniversalTime());
        intersection.End.Should().Be(baseTime.AddHours(3).ToUniversalTime());
    }
}
