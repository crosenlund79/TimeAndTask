using WorkTrace.Core.Entities;

namespace WorkTrace.Core.Management;

/// <summary>
/// A recurring minor task's schedule (local time). Interval reminders fall at <see cref="ActiveFrom"/> + k × interval (k ≥ 1)
/// up to <see cref="ActiveUntil"/> on active days; daily ones at <see cref="DailyAt"/> on active days. Only the next occurrence
/// is ever stored, so missed occurrences are never replayed.
/// </summary>
public sealed record MinorTaskRecurrence(
    MinorTaskRepeat Kind,
    int IntervalMinutes = 60,
    TimeOnly DailyAt = default,
    int Days = MinorTaskRecurrenceDays.Weekdays,
    TimeOnly ActiveFrom = default,
    TimeOnly ActiveUntil = default)
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 24 * 60;

    /// <summary>Every hour, Monday-Friday 08:00-16:00.</summary>
    public static MinorTaskRecurrence Default { get; } = new(MinorTaskRepeat.Interval, 60, new TimeOnly(10, 0), MinorTaskRecurrenceDays.Weekdays,
        new TimeOnly(8, 0), new TimeOnly(16, 0));

    public bool IsActiveDay(DayOfWeek day) => (Days & MinorTaskRecurrenceDays.Bit(day)) != 0;

    /// <summary>The first occurrence strictly after <paramref name="afterLocal"/>; null if no day is active.</summary>
    public DateTime? NextLocal(DateTime afterLocal)
    {
        for (var i = 0; i <= 7; i++)
        {
            var day = afterLocal.Date.AddDays(i);
            if (!IsActiveDay(day.DayOfWeek))
            {
                continue;
            }

            if (Kind == MinorTaskRepeat.Daily)
            {
                var at = day + DailyAt.ToTimeSpan();
                if (at > afterLocal)
                {
                    return at;
                }

                continue;
            }

            var from = day + ActiveFrom.ToTimeSpan();
            var until = day + ActiveUntil.ToTimeSpan();
            var step = TimeSpan.FromMinutes(Math.Max(MinIntervalMinutes, IntervalMinutes));
            var k = afterLocal < from ? 1 : Math.Max(1, (long)Math.Floor((afterLocal - from) / step) + 1);
            var next = from + (step * k);
            if (next <= until)
            {
                return next;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a due occurrence may still be shown at <paramref name="nowLocal"/>: interval reminders only within the active
    /// hours of an active day, a daily one only on its own day. Otherwise it is skipped to the next occurrence.
    /// </summary>
    public bool MayShow(DateTime nowLocal, DateTime dueLocal) => Kind == MinorTaskRepeat.Daily
        ? nowLocal.Date == dueLocal.Date
        : IsActiveDay(nowLocal.DayOfWeek) && TimeOnly.FromDateTime(nowLocal) is var t && t >= ActiveFrom && t <= ActiveUntil;

    public DateTime? NextUtc(DateTime afterUtc, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (NextLocal(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone)) is not { } local)
        {
            return null;
        }

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1); // skipped by a daylight saving change
        }

        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, zone), DateTimeKind.Utc);
    }

    /// <summary>"Every 30 min · Mon-Fri 08:00-16:00", "Daily 10:00 · Mon-Fri".</summary>
    public string Describe()
    {
        var days = Days switch
        {
            MinorTaskRecurrenceDays.Weekdays => "Mon–Fri",
            MinorTaskRecurrenceDays.All => "every day",
            _ => string.Join(" ", Enumerable.Range(0, 7).Where(i => (Days & (1 << i)) != 0)
                .Select(i => ((DayOfWeek)((i + 1) % 7)).ToString()[..3])),
        };
        return Kind == MinorTaskRepeat.Daily
            ? $"Daily {H(DailyAt)} · {days}"
            : $"Every {(IntervalMinutes % 60 == 0 ? $"{IntervalMinutes / 60} h" : $"{IntervalMinutes} min")} · {days} {H(ActiveFrom)}–{H(ActiveUntil)}";
    }

    private static string H(TimeOnly time) => time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}
