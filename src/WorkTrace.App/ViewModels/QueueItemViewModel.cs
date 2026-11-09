using System.Globalization;
using System.Windows;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>A working day on the timeline axis (dotted boundary, weekday/date label, holiday highlight).</summary>
public sealed class DayCellViewModel(PlanDay day)
{
    public DateOnly Date { get; } = day.Date;

    public string Label { get; } = day.Date.ToString("ddd d/M", CultureInfo.CurrentCulture);

    /// <summary>Official Danish public holiday: no work is planned on it.</summary>
    public bool IsPublicHoliday { get; } = day.Holiday?.Kind == HolidayKind.Public;

    /// <summary>Customary day off (not an official holiday): still planned as a working day, flagged as uncertain.</summary>
    public bool IsCustomaryHoliday { get; } = day.Holiday?.Kind == HolidayKind.Customary;

    public string ToolTip { get; } = day.Holiday switch
    {
        { Kind: HolidayKind.Public } h => $"{day.Date:dddd d MMMM}: {h.Name} (public holiday, no work planned)",
        { } h => $"{day.Date:dddd d MMMM}: {h.Name}? Often a day off, but not an official public holiday (planned as a working day)",
        _ => $"{day.Date:dddd d MMMM} · 7.5 working hours",
    };
}

/// <summary>A row of the planning timeline: one task as a bar on the working-day axis (estimated or provisional effort).</summary>
public sealed class QueueItemViewModel(QueueEntry entry, IReadOnlyList<DayCellViewModel> days)
{
    public QueueEntry Entry { get; } = entry;

    public int Id => Entry.Task.Id;

    public int Position => Entry.Position;

    public string Title => Entry.Task.Title;

    public string Context => Entry.Task.ExternalId is { Length: > 0 } ext ? $"{ext} · {Entry.Task.ClientName}" : Entry.Task.ClientName;

    public string StakeholderColor => Entry.Task.ClientColor;

    /// <summary>No estimate: shown red and dashed with a provisional block (never stored).</summary>
    public bool IsProvisional => Entry.IsProvisional;

    public string DurationText => IsProvisional ? "4 h? · no estimate" : TaskPipeline.FormatDuration(Entry.DurationMinutes);

    public string StartText => Entry.StartDate == Entry.EndDate
        ? Entry.StartDate.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        : $"{Entry.StartDate.ToString("ddd d MMM", CultureInfo.CurrentCulture)} – {Entry.EndDate.ToString("ddd d MMM", CultureInfo.CurrentCulture)}";

    public string? PlannedText => Entry.Task.PlannedStart is { } d ? "planned " + d.ToString("d MMM", CultureInfo.CurrentCulture) : null;

    public bool IsWaiting => Entry.Task.IsWaiting;

    /// <summary>A concrete deadline: the title is bold (the deadline never moves the bar).</summary>
    public bool HasDeadline => Entry.Task.Deadline is not null;

    public bool IsOverdue => Entry.Task.DeadlineState == DeadlineState.Overdue;

    public string? DeadlineText => Entry.Task.Deadline is { } d
        ? (IsOverdue ? "overdue since " : "due ") + d.ToString("d MMM", CultureInfo.CurrentCulture)
        : null;

    /// <summary>The axis' working days (shared by all rows), drawn behind the bar.</summary>
    public IReadOnlyList<DayCellViewModel> Days { get; } = days;

    /// <summary>Weekday/date labels are shown once, above the first row.</summary>
    public bool ShowDayLabels => Entry.Position == 1;

    public GridLength Before { get; } = new(Math.Max(0, entry.StartDay), GridUnitType.Star);

    public GridLength Block { get; } = new(Math.Max(0.02, entry.EndDay - entry.StartDay), GridUnitType.Star);

    public GridLength After { get; } = new(Math.Max(0, days.Count - entry.EndDay), GridUnitType.Star);
}
