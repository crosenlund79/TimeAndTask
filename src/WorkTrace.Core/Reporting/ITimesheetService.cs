namespace WorkTrace.Core.Reporting;

/// <summary>One day of one line. <see cref="Minutes"/> is what counts: the correction if there is one, else the estimate.</summary>
public sealed record TimesheetCell(int ComputedMinutes, int Minutes, bool IsAdjusted, bool BaselineChanged);

/// <summary>A timesheet line: one task (or "no task") with its stakeholder and external reference, Monday to Sunday.</summary>
public sealed record TimesheetLine(
    int? TaskId,
    string Stakeholder,
    string System,
    string Reference,
    string Task,
    string Comments,
    IReadOnlyList<TimesheetCell> Days)
{
    public int TotalMinutes => Days.Sum(d => d.Minutes);
}

public sealed record TimesheetWeek(
    DateOnly Monday,
    IReadOnlyList<TimesheetLine> Lines,
    IReadOnlyList<int> UnclassifiedMinutes,
    int MissedCheckIns)
{
    public IReadOnlyList<DateOnly> Days { get; } = [.. Enumerable.Range(0, 7).Select(Monday.AddDays)];

    public IReadOnlyList<int> DayTotals => [.. Enumerable.Range(0, 7).Select(i => Lines.Sum(l => l.Days[i].Minutes))];

    public int WeekTotal => Lines.Sum(l => l.TotalMinutes);

    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
}

/// <summary>Weekly timesheet built from the estimated timeline, with per-day corrections and exports.</summary>
public interface ITimesheetService
{
    Task<TimesheetWeek> GetWeekAsync(DateOnly anyDay, CancellationToken cancellationToken = default);

    /// <summary>Corrects one day of one task (minutes), or removes the correction (null) so the estimate counts again.</summary>
    Task SetMinutesAsync(DateOnly day, int taskId, int? minutes, int computedMinutes, CancellationToken cancellationToken = default);

    /// <summary>One row per day and task (date, stakeholder, system, reference, task, hours, comments), for pasting or import.</summary>
    Task ExportCsvAsync(TimesheetWeek week, string path, CancellationToken cancellationToken = default);

    /// <summary>Excel workbook: sheet "Week" (lines × Monday–Sunday) and sheet "Lines" (as the CSV).</summary>
    Task ExportExcelAsync(TimesheetWeek week, string path, CancellationToken cancellationToken = default);
}

public static class Hours
{
    /// <summary>Decimal hours, as most time-registration systems expect (90 min → 1.50).</summary>
    public static decimal FromMinutes(int minutes) => Math.Round(minutes / 60m, 2);

    /// <summary>Parses "1.5", "1,5", "1:30" or "90m" into minutes; null for empty or invalid text.</summary>
    public static int? ParseMinutes(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.EndsWith('m') && int.TryParse(t[..^1], System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 0)
        {
            return m;
        }

        if (t.Contains(':', StringComparison.Ordinal))
        {
            var parts = t.Split(':');
            return parts.Length == 2 && int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var h)
                && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var mm) && h >= 0 && mm is >= 0 and < 60
                ? (h * 60) + mm
                : null;
        }

        if (decimal.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            && hours >= 0 && hours <= 24)
        {
            return (int)Math.Round(hours * 60);
        }

        return null;
    }
}
