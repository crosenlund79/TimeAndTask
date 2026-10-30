using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.Tests.Core;

public class TimeRegistrationRemindersTests
{
    private static readonly TimeOnly Start = new(15, 0);
    private static readonly TimeOnly End = new(8, 0);

    [Theory]
    [InlineData("2026-10-26 14:59", null)]
    [InlineData("2026-10-26 15:00", "2026-10-26")]
    [InlineData("2026-10-26 23:30", "2026-10-26")]
    [InlineData("2026-10-27 02:00", "2026-10-26")] // after midnight: still Monday's period
    [InlineData("2026-10-27 07:59", "2026-10-26")]
    [InlineData("2026-10-27 08:00", null)]
    public void PeriodCrossingMidnight_BelongsToTheDayItStarted(string local, string? expected) =>
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            TimeRegistrationReminders.PeriodDay(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), Start, End));

    [Fact]
    public void Due_OnlyInPeriod_NotAfterDone_AndNotBeforeSnoozeEnds()
    {
        var utc = TimeZoneInfo.Utc;
        var s = new ReminderSettings { Enabled = true };
        var afternoon = new DateTime(2026, 10, 26, 16, 0, 0, DateTimeKind.Utc);

        Assert.True(TimeRegistrationReminders.IsDue(afternoon, utc, s));
        Assert.False(TimeRegistrationReminders.IsDue(afternoon.AddHours(-3), utc, s));

        s.NotBeforeUtc = afternoon.AddMinutes(60);
        Assert.False(TimeRegistrationReminders.IsDue(afternoon.AddMinutes(30), utc, s));
        Assert.True(TimeRegistrationReminders.IsDue(afternoon.AddMinutes(61), utc, s));

        s.DoneForDay = new DateOnly(2026, 10, 26); // "Yes, all done" or "Remind me tomorrow"
        Assert.False(TimeRegistrationReminders.IsDue(afternoon.AddHours(10), utc, s)); // 02:00 next morning
        Assert.True(TimeRegistrationReminders.IsDue(afternoon.AddDays(1), utc, s));     // next working day's period

        Assert.False(TimeRegistrationReminders.IsDue(afternoon, utc, new ReminderSettings()));
    }
}
