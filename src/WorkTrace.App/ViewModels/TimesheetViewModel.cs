using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Common;
using WorkTrace.Core.Reporting;

namespace WorkTrace.App.ViewModels;

public enum TimesheetRowKind
{
    Task = 0,
    Total = 1,
    Unclassified = 2,
}

/// <summary>One day cell. Task cells are editable: typing hours saves a correction, clearing it restores the estimate.</summary>
public sealed partial class TimesheetCellViewModel(TimesheetRowViewModel row, int day, string text, bool editable, bool adjusted, bool baselineChanged)
    : ObservableObject
{
    private string _text = text;

    public bool IsEditable { get; } = editable;

    public bool IsAdjusted { get; } = adjusted;

    public bool BaselineChanged { get; } = baselineChanged;

    public string? ToolTip => BaselineChanged ? "Corrected by you; the underlying check-ins changed since. Clear the cell to use the new estimate."
        : IsAdjusted ? "Corrected by you (clear the cell to use the estimate again)"
        : IsEditable ? "Estimated from check-ins. Type hours (1.5 or 1:30) to correct." : null;

    public string Text
    {
        get => _text;
        set
        {
            if (value != _text && IsEditable)
            {
                _text = value;
                _ = row.CellEditedAsync(day, value);
            }
        }
    }
}

public sealed class TimesheetRowViewModel
{
    private readonly Func<TimesheetRowViewModel, int, string, Task>? _onEdit;

    public TimesheetRowViewModel(TimesheetRowKind kind, TimesheetLine? line, string task, IReadOnlyList<int> minutes, Func<TimesheetRowViewModel, int, string, Task>? onEdit = null)
    {
        Kind = kind;
        Line = line;
        Task = task;
        _onEdit = onEdit;
        Cells = [.. minutes.Select((m, i) => new TimesheetCellViewModel(
            this, i, Format(m), kind == TimesheetRowKind.Task && line?.TaskId is not null,
            line?.Days[i].IsAdjusted ?? false, line?.Days[i].BaselineChanged ?? false))];
        TotalText = Format(minutes.Sum());
    }

    public TimesheetRowKind Kind { get; }

    public TimesheetLine? Line { get; }

    public string Stakeholder => Line?.Stakeholder ?? "";

    public string Reference => Line is null ? "" : string.Join(" ", new[] { Line.System, Line.Reference }.Where(x => x.Length > 0));

    public string Task { get; }

    public string Comments => Line?.Comments ?? "";

    public IReadOnlyList<TimesheetCellViewModel> Cells { get; }

    public string TotalText { get; }

    public bool IsTotal => Kind == TimesheetRowKind.Total;

    public bool IsUnclassified => Kind == TimesheetRowKind.Unclassified;

    internal Task CellEditedAsync(int day, string text) => _onEdit?.Invoke(this, day, text) ?? System.Threading.Tasks.Task.CompletedTask;

    public static string Format(int minutes) => minutes == 0 ? "" : Hours.FromMinutes(minutes).ToString("0.00", CultureInfo.CurrentCulture);
}

/// <summary>
/// Weekly timesheet (Monday–Sunday): one line per task with its stakeholder and reference, hours per day estimated from
/// check-ins, corrections typed straight into the cells, unclassified time per day, and Excel/CSV export.
/// </summary>
public sealed partial class TimesheetViewModel(
    ITimesheetService timesheets,
    IFileDialogService files,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<TimesheetViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    private TimesheetWeek? _week;

    public bool HasUnsavedChanges => false;

    public ObservableCollection<TimesheetRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial DateOnly Monday { get; private set; }

    [ObservableProperty]
    public partial string WeekTitle { get; private set; } = "";

    [ObservableProperty]
    public partial IReadOnlyList<string> DayHeaders { get; private set; } = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial string? Warning { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local));

    public async Task ActivateAsync(object? parameter) => await LoadAsync(Monday == default ? Today : Monday);

    [RelayCommand]
    private Task PreviousWeekAsync() => LoadAsync(Monday.AddDays(-7));

    [RelayCommand]
    private Task NextWeekAsync() => LoadAsync(Monday.AddDays(7));

    [RelayCommand]
    private Task ThisWeekAsync() => LoadAsync(Today);

    public async Task LoadAsync(DateOnly day)
    {
        TimesheetWeek? week = null;
        if (!await RunAsync(async () => week = await timesheets.GetWeekAsync(day), "Could not build the timesheet."))
        {
            return;
        }

        _week = week!;
        Monday = week!.Monday;
        var culture = CultureInfo.CurrentCulture;
        var weekNumber = ISOWeek.GetWeekOfYear(week.Monday.ToDateTime(TimeOnly.MinValue));
        WeekTitle = $"Week {weekNumber} · {week.Monday.ToString("d MMM", culture)} – {week.Days[6].ToString("d MMM yyyy", culture)}";
        DayHeaders = [.. week.Days.Select(d => d.ToString("ddd d/M", culture))];

        Rows.Clear();
        foreach (var line in week.Lines)
        {
            Rows.Add(new TimesheetRowViewModel(TimesheetRowKind.Task, line, line.Task, [.. line.Days.Select(c => c.Minutes)], SaveCellAsync));
        }

        Rows.Add(new TimesheetRowViewModel(TimesheetRowKind.Total, null, "Total", week.DayTotals));
        if (week.UnclassifiedMinutes.Sum() > 0)
        {
            Rows.Add(new TimesheetRowViewModel(TimesheetRowKind.Unclassified, null, "Unclassified – tracked, but no check-in says what", week.UnclassifiedMinutes));
        }

        IsEmpty = week.Lines.Count == 0;
        var total = TimesheetRowViewModel.Format(week.WeekTotal);
        Summary = $"{(total.Length > 0 ? total : "0")} hours this week";
        var unclassified = week.UnclassifiedMinutes.Sum();
        Warning = unclassified > 0 || week.MissedCheckIns > 0
            ? $"{TimesheetRowViewModel.Format(unclassified)} h unclassified" + (week.MissedCheckIns > 0 ? $" · {week.MissedCheckIns} missed check-in(s)" : "")
              + ". Correct the cells or add check-ins for those hours."
            : null;
    }

    private async Task SaveCellAsync(TimesheetRowViewModel row, int day, string text)
    {
        if (_week is null || row.Line?.TaskId is not { } taskId)
        {
            return;
        }

        var minutes = Hours.ParseMinutes(text);
        if (minutes is null && !string.IsNullOrWhiteSpace(text))
        {
            Notifications.Show("Type hours like 1.5, 1,5 or 1:30 (empty = use the estimate).", NotificationKind.Warning);
            await LoadAsync(Monday);
            return;
        }

        await RunAsync(() => timesheets.SetMinutesAsync(_week.Days[day], taskId, minutes, row.Line.Days[day].ComputedMinutes), "Could not save the correction.");
        await LoadAsync(Monday);
    }

    [RelayCommand]
    private Task ExportExcelAsync() => ExportAsync("Excel workbook (*.xlsx)|*.xlsx", "xlsx", timesheets.ExportExcelAsync);

    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("CSV (*.csv)|*.csv", "csv", timesheets.ExportCsvAsync);

    private async Task ExportAsync(string filter, string extension, Func<TimesheetWeek, string, CancellationToken, Task> export)
    {
        if (_week is null)
        {
            return;
        }

        var week = ISOWeek.GetWeekOfYear(_week.Monday.ToDateTime(TimeOnly.MinValue));
        var path = files.SaveFile("Export timesheet", filter, $"WorkTrace-timesheet-{ISOWeek.GetYear(_week.Monday.ToDateTime(TimeOnly.MinValue))}-W{week:00}.{extension}");
        if (path is not null && await RunAsync(() => export(_week, path, CancellationToken.None), "Could not export the timesheet."))
        {
            Notifications.Show($"Exported to {System.IO.Path.GetFileName(path)}.", NotificationKind.Success, "Open", () =>
            {
                files.OpenFile(path);
                return Task.CompletedTask;
            });
        }
    }
}
