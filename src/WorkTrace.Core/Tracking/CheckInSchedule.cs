using WorkTrace.Core.Common;

namespace WorkTrace.Core.Tracking;

/// <summary>
/// Wall-clock aligned check-in slots: with a 30-minute interval prompts fall on :00 and :30 local
/// time, which makes observations line up with how people think about their day. Slots restart at
/// local midnight, so intervals that do not divide a day still behave predictably.
/// </summary>
public static class CheckInSchedule
{
    /// <summary>
    /// The earliest slot instant strictly after <paramref name="afterUtc"/>. Local times that do not exist
    /// (spring-forward gap) are skipped; ambiguous local times (fall-back) occur twice.
    /// </summary>
    public static DateTime NextSlotUtc(DateTime afterUtc, int intervalMinutes, TimeZoneInfo timeZone)
    {
        afterUtc = UtcTime.Ensure(afterUtc);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalMinutes, 1);
        ArgumentNullException.ThrowIfNull(timeZone);

        var localDay = TimeZoneInfo.ConvertTimeFromUtc(afterUtc, timeZone).Date;
        DateTime? best = null;

        // Around a fall-back transition the same local slot maps to two instants, so slot order is not
        // instant order: take the minimum over a small window of local days (a few hundred candidates).
        for (var day = localDay.AddDays(-1); day <= localDay.AddDays(2); day = day.AddDays(1))
        {
            for (var minutes = 0; minutes < 24 * 60; minutes += intervalMinutes)
            {
                var local = DateTime.SpecifyKind(day.AddMinutes(minutes), DateTimeKind.Unspecified);
                if (timeZone.IsInvalidTime(local))
                {
                    continue;
                }

                foreach (var candidate in ToUtcCandidates(local, timeZone))
                {
                    if (candidate > afterUtc && (best is null || candidate < best))
                    {
                        best = candidate;
                    }
                }
            }
        }

        return best ?? throw new InvalidOperationException("No check-in slot found.");
    }

    private static IEnumerable<DateTime> ToUtcCandidates(DateTime local, TimeZoneInfo timeZone)
    {
        if (!timeZone.IsAmbiguousTime(local))
        {
            yield return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
            yield break;
        }

        // Ambiguous: the same wall-clock time happens twice. Return both, earliest first.
        foreach (var offset in timeZone.GetAmbiguousTimeOffsets(local).OrderByDescending(o => o))
        {
            yield return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
    }
}
