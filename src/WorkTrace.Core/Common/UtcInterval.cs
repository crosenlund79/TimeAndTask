namespace WorkTrace.Core.Common;

/// <summary>
/// Half-open UTC interval [Start, End). Half-open intervals let adjacent periods share a
/// boundary without double-counting it.
/// </summary>
public readonly record struct UtcInterval
{
    public UtcInterval(DateTime start, DateTime end)
    {
        start = UtcTime.Ensure(start, nameof(start));
        end = UtcTime.Ensure(end, nameof(end));
        if (end < start)
        {
            throw new ArgumentException($"Interval end ({end:O}) precedes start ({start:O}).", nameof(end));
        }

        Start = start;
        End = end;
    }

    public DateTime Start { get; }

    public DateTime End { get; }

    public TimeSpan Duration => End - Start;

    public bool IsEmpty => End == Start;

    public bool Contains(DateTime instant)
    {
        instant = UtcTime.Ensure(instant, nameof(instant));
        return instant >= Start && instant < End;
    }

    public bool Overlaps(UtcInterval other) => Start < other.End && other.Start < End;

    public UtcInterval? Intersect(UtcInterval other)
    {
        var start = Start > other.Start ? Start : other.Start;
        var end = End < other.End ? End : other.End;
        return start < end ? new UtcInterval(start, end) : null;
    }

    public override string ToString() => $"[{Start:O}, {End:O})";
}
