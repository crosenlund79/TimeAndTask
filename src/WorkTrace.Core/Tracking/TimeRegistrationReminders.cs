using WorkTrace.Core.Settings;

namespace WorkTrace.Core.Tracking;

/// <summary>
/// When to ask "Have you registered your time today?". The period may cross midnight (15:00–08:00): a time
/// belongs to the working day on which its period started, so 02:00 on Tuesday is still Monday's period.
/// </summary>
public static class TimeRegistrationReminders
{
    /// <summary>The working day whose reminder period contains <paramref name="local"/>, or null outside the period.</summary>
    public static DateOnly? PeriodDay(DateTime local, TimeOnly start, TimeOnly end)
    {
        var time = TimeOnly.FromDateTime(local);
        var date = DateOnly.FromDateTime(local);
        if (start == end)
        {
            return time >= start ? date : date.AddDays(-1); // a full day starting at "start"
        }

        if (start < end)
        {
            return time >= start && time < end ? date : null;
        }

        return time >= start ? date : time < end ? date.AddDays(-1) : null;
    }

    /// <summary>The working day to mark done for answers given now: the current period's day, else today.</summary>
    public static DateOnly AnswerDay(DateTime local, ReminderSettings settings) =>
        PeriodDay(local, settings.Start, settings.End) ?? DateOnly.FromDateTime(local);

    /// <summary>True when a periodic reminder should be shown now.</summary>
    public static bool IsDue(DateTime nowUtc, TimeZoneInfo timeZone, ReminderSettings settings)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone);
        var day = PeriodDay(local, settings.Start, settings.End);
        return day is not null && settings.DoneForDay != day && (settings.NotBeforeUtc is not { } notBefore || nowUtc >= notBefore);
    }

    /// <summary>True when time registration for the current working day is still outstanding (for the shutdown reminder).</summary>
    public static bool IsOutstanding(DateTime nowUtc, TimeZoneInfo timeZone, ReminderSettings settings) =>
        settings.DoneForDay != AnswerDay(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone), settings);

    private static readonly string[] Messages =
    [
        "Future you will thank you in the future 👍🏻 And so will your boss 😇🔫",
        "Today's hours won't register themselves. Unfortunately.",
        "Your future self would prefer not to reconstruct today's work tomorrow.",
        "The clock remembers. Your timesheet might not. ⏰",
        "Quick one before you forget: hours in, conscience clear. ✨",
    ];

    private static readonly string[] ShutdownMessages =
    [
        "Before you disappear... have you registered your time? 😇",
        "Before you escape... did you remember your timesheet?",
    ];

    /// <summary>Rotates through the messages (by a counter, so consecutive reminders differ).</summary>
    public static string Message(int counter, bool shutdown = false)
    {
        var list = shutdown ? ShutdownMessages : Messages;
        return list[(int)((uint)counter % (uint)list.Length)];
    }
}

/// <summary>
/// Weekly reporting reminder ("register the week's hours and send next week's status"): due from the configured day and time
/// until the end of that week (Sunday), unless that week was answered "Done for this week".
/// </summary>
public static class WeeklyReportReminder
{
    public const string Message = "Har du husket at registrere ugens timer og sende status for næste uge til Bruno? ☕";

    /// <summary>Monday of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static bool IsDue(DateTime local, ReminderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var today = DateOnly.FromDateTime(local);
        var week = WeekOf(today);
        if (!settings.WeeklyReportEnabled || settings.WeeklyReportDoneWeek == week)
        {
            return false;
        }

        var due = week.AddDays(((int)settings.WeeklyReportDay + 6) % 7).ToDateTime(settings.WeeklyReportTime);
        return local >= due;
    }
}
