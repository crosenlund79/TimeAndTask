using WorkTrace.Core.Common;

namespace WorkTrace.Tests.Core;

public class UtcTimeTests
{
    [Fact]
    public void Ensure_KeepsUtc()
    {
        var value = new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Utc);
        Assert.Equal(value, UtcTime.Ensure(value));
    }

    [Fact]
    public void Ensure_ConvertsLocalToUtc()
    {
        var local = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
        var result = UtcTime.Ensure(local);
        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(local.ToUniversalTime(), result);
    }

    [Fact]
    public void Ensure_RejectsUnspecified()
    {
        var unspecified = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Unspecified);
        Assert.Throws<ArgumentException>(() => UtcTime.Ensure(unspecified));
    }

    [Fact]
    public void Interval_IsHalfOpen()
    {
        var start = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        var interval = new UtcInterval(start, start.AddMinutes(30));

        Assert.True(interval.Contains(start));
        Assert.False(interval.Contains(start.AddMinutes(30)));
        Assert.Equal(TimeSpan.FromMinutes(30), interval.Duration);
    }

    [Fact]
    public void Interval_AdjacentIntervalsDoNotOverlap()
    {
        var t = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        var a = new UtcInterval(t, t.AddMinutes(15));
        var b = new UtcInterval(t.AddMinutes(15), t.AddMinutes(30));

        Assert.False(a.Overlaps(b));
        Assert.Null(a.Intersect(b));
    }

    [Fact]
    public void Interval_Intersect()
    {
        var t = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        var a = new UtcInterval(t, t.AddMinutes(30));
        var b = new UtcInterval(t.AddMinutes(20), t.AddMinutes(60));

        Assert.Equal(new UtcInterval(t.AddMinutes(20), t.AddMinutes(30)), a.Intersect(b));
    }

    [Fact]
    public void Interval_RejectsReversedRange()
    {
        var t = new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);
        Assert.Throws<ArgumentException>(() => new UtcInterval(t, t.AddTicks(-1)));
    }
}
