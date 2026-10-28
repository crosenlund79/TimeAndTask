using WorkTrace.Core.Tracking;

namespace WorkTrace.Tests.Tracking;

public class CheckInScheduleTests
{
    private static DateTime Utc(int month, int day, int hour, int minute, int second = 0) =>
        new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    /// <summary>Central European Time with EU daylight-saving rules, built in code so tests don't depend on OS time zone data.</summary>
    internal static readonly TimeZoneInfo Cet = TimeZoneInfo.CreateCustomTimeZone(
        "Test/CET",
        TimeSpan.FromHours(1),
        "CET",
        "CET",
        "CEST",
        [
            TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
                new DateTime(2000, 1, 1),
                new DateTime(2099, 12, 31),
                TimeSpan.FromHours(1),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 5, DayOfWeek.Sunday),
                TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 3, 0, 0), 10, 5, DayOfWeek.Sunday)),
        ]);

    [Theory]
    [InlineData(9, 7, 9, 30)]
    [InlineData(9, 29, 9, 30)]
    [InlineData(9, 30, 10, 0)] // exactly on a slot: the next one
    [InlineData(23, 45, 0, 0)]
    public void Slots_AreAlignedToTheClock(int hour, int minute, int expectedHour, int expectedMinute)
    {
        var next = CheckInSchedule.NextSlotUtc(Utc(10, 28, hour, minute), 30, TimeZoneInfo.Utc);
        Assert.Equal(expectedHour, next.Hour);
        Assert.Equal(expectedMinute, next.Minute);
        Assert.Equal(DateTimeKind.Utc, next.Kind);
    }

    [Fact]
    public void IntervalNotDividingTheDay_RestartsAtMidnight()
    {
        // 45-minute slots: ..., 23:15, then 00:00 (not 00:00 + 45 minutes carried over).
        Assert.Equal(Utc(10, 28, 23, 15), CheckInSchedule.NextSlotUtc(Utc(10, 28, 22, 50), 45, TimeZoneInfo.Utc));
        Assert.Equal(Utc(10, 29, 0, 0), CheckInSchedule.NextSlotUtc(Utc(10, 28, 23, 20), 45, TimeZoneInfo.Utc));
    }

    [Fact]
    public void SlotsFollowLocalTime()
    {
        // 09:07 CET (winter, UTC+1) = 08:07Z → next local slot 09:30 = 08:30Z.
        Assert.Equal(Utc(11, 2, 8, 30), CheckInSchedule.NextSlotUtc(Utc(11, 2, 8, 7), 30, Cet));
    }

    [Fact]
    public void SpringForwardGap_IsSkipped()
    {
        // 29 Mar 2026: local 02:00 → 03:00. After 01:45 CET (00:45Z) the slots 02:00/02:30 don't exist.
        var next = CheckInSchedule.NextSlotUtc(Utc(3, 29, 0, 45), 30, Cet);
        Assert.Equal(Utc(3, 29, 1, 0), next); // 03:00 CEST
    }

    [Fact]
    public void FallBack_RepeatedHour_IsWalkedInRealTimeOrder()
    {
        // 25 Oct 2026: local 03:00 CEST → 02:00 CET; 02:00–03:00 local happens twice.
        var t = Utc(10, 25, 0, 10); // 02:10 CEST
        var slots = new List<DateTime>();
        for (var i = 0; i < 4; i++)
        {
            t = CheckInSchedule.NextSlotUtc(t, 30, Cet);
            slots.Add(t);
        }

        Assert.Equal([Utc(10, 25, 0, 30), Utc(10, 25, 1, 0), Utc(10, 25, 1, 30), Utc(10, 25, 2, 0)], slots);
    }
}
