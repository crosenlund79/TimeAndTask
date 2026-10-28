using System.Globalization;

namespace WorkTrace.Core.Common;

/// <summary>Compact, human-friendly "time ago" labels for lists (e.g. "5 min ago", "yesterday").</summary>
public static class RelativeTime
{
    public static string Format(DateTime? utc, DateTime nowUtc, TimeZoneInfo timeZone, string never = "never")
    {
        if (utc is not { } value)
        {
            return never;
        }

        value = UtcTime.Ensure(value);
        nowUtc = UtcTime.Ensure(nowUtc);
        var age = nowUtc - value;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        var localValue = TimeZoneInfo.ConvertTimeFromUtc(value, timeZone).Date;
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, timeZone).Date;
        var days = (localToday - localValue).Days;

        if (days == 0)
        {
            return $"{(int)age.TotalHours} h ago";
        }

        if (days == 1)
        {
            return "yesterday";
        }

        if (days < 7)
        {
            return $"{days} days ago";
        }

        return localValue.Year == localToday.Year
            ? localValue.ToString("d MMM", CultureInfo.CurrentCulture)
            : localValue.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
