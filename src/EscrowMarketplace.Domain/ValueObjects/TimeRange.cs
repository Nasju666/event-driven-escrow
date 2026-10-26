namespace EscrowMarketplace.Domain.ValueObjects;

public record TimeRange
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }

    public TimeSpan Duration => End - Start;

    public TimeRange(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentException($"End time ({end}) must be strictly greater than start time ({start}).", nameof(end));
        }

        Start = start.ToUniversalTime();
        End = end.ToUniversalTime();
    }

    // Required for EF Core materialization
    protected TimeRange() { }

    /// <summary>
    /// Evaluates if two intervals share any common point in time [Start, End).
    /// Equivalent to PostgreSQL && operator.
    /// </summary>
    public bool Overlaps(TimeRange other)
    {
        return Start < other.End && End > other.Start;
    }

    /// <summary>
    /// Checks if a given timestamp falls within [Start, End).
    /// Equivalent to PostgreSQL @> operator with timestamp.
    /// </summary>
    public bool Contains(DateTimeOffset timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return utc >= Start && utc < End;
    }

    /// <summary>
    /// Checks if another range is strictly contained inside this range.
    /// Equivalent to PostgreSQL @> operator with range.
    /// </summary>
    public bool Contains(TimeRange other)
    {
        return Start <= other.Start && End >= other.End;
    }

    /// <summary>
    /// Checks if two intervals are immediately adjacent without overlap [t1, t2) and [t2, t3).
    /// Equivalent to PostgreSQL -|- operator.
    /// </summary>
    public bool IsAdjacentTo(TimeRange other)
    {
        return End == other.Start || Start == other.End;
    }

    /// <summary>
    /// Computes overlapping intersection window.
    /// </summary>
    public TimeRange? Intersect(TimeRange other)
    {
        if (!Overlaps(other)) return null;
        var maxStart = Start > other.Start ? Start : other.Start;
        var minEnd = End < other.End ? End : other.End;
        return new TimeRange(maxStart, minEnd);
    }

    public override string ToString() => $"[{Start:O}, {End:O})";
}
