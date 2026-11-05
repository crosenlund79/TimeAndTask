using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
using WorkTrace.Core.Management;
using WorkTrace.Core.Services;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.ViewModels;

/// <summary>Overview page: storage status, quick actions and what's coming next.</summary>
public sealed partial class HomeViewModel : ViewModelBase, ISectionViewModel
{
    private readonly IAppStatusService _statusService;
    private readonly ICheckInService _checkIns;
    private readonly INavigationService _navigation;
    private readonly TimeProvider _timeProvider;
    private readonly ITaskService _tasks;
    private readonly WorkTrace.Core.Reporting.ITimesheetService _timesheets;
    private bool _loadingCurrent;
    private int? _recordedTaskId;

    public HomeViewModel(
        IAppStatusService statusService,
        ICheckInService checkIns,
        ITaskService tasks,
        WorkTrace.Core.Reporting.ITimesheetService timesheets,
        TrackingController tracking,
        INavigationService navigation,
        INotificationService notifications,
        TimeProvider timeProvider,
        ILogger<HomeViewModel> logger)
        : base(notifications, logger)
    {
        _statusService = statusService;
        _checkIns = checkIns;
        _navigation = navigation;
        _timeProvider = timeProvider;
        _tasks = tasks;
        _timesheets = timesheets;
        Tracking = tracking;

        // Keep "Today" current while the page is open.
        tracking.CheckInRecorded += (_, _) =>
        {
            _ = LoadTimelineAsync();
            _ = LoadCurrentTaskAsync();
        };
        tracking.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TrackingController.Status))
            {
                _ = LoadTimelineAsync();
            }
        };
    }

    public TrackingController Tracking { get; }

    public ObservableCollection<TimelineItemViewModel> Today { get; } = [];

    /// <summary>Non-archived tasks, most recently used first, for the "Current task" lookup.</summary>
    public ObservableCollection<CurrentTaskOption> CurrentTasks { get; } = [];

    /// <summary>The task of the latest check-in; choosing another one records a check-in now.</summary>
    [ObservableProperty]
    public partial int CurrentTaskId { get; set; }

    [ObservableProperty]
    public partial string? CurrentTaskText { get; set; }

    [ObservableProperty]
    public partial string? TodaySummary { get; private set; }

    [ObservableProperty]
    public partial AppStatus? Status { get; private set; }

    public string Version { get; } = typeof(HomeViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool HasUnsavedChanges => false;

    /// <summary>Today's totals (label, value, hint) for the Daily summary card.</summary>
    public ObservableCollection<SummaryRow> DailySummary { get; } = [];

    public async Task ActivateAsync(object? parameter)
    {
        await RunAsync(async () => Status = await _statusService.GetStatusAsync(), "Could not read the database status.");
        await LoadTimelineAsync();
        await LoadCurrentTaskAsync();
    }

    private DateOnly CurrentDay => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));

    /// <summary>The day shown (defaults to today). DatePicker-friendly; any earlier day can be chosen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsToday), nameof(DayTitle))]
    [NotifyCanExecuteChangedFor(nameof(NextDayCommand), nameof(GoToTodayCommand))]
    public partial DateTime? SelectedDate { get; set; }

    [ObservableProperty]
    public partial bool HasNoRegistrations { get; private set; }

    public DateOnly SelectedDay => SelectedDate is { } d ? DateOnly.FromDateTime(d) : CurrentDay;

    public bool IsToday => SelectedDay == CurrentDay;

    public string DayTitle => IsToday ? "Today" : SelectedDay.ToString("dddd d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Registrations of the selected day (the part inside the day for intervals spanning midnight).</summary>
    public ObservableCollection<RegistrationItemViewModel> Registrations { get; } = [];

    [ObservableProperty]
    public partial string? RegistrationTotal { get; private set; }

    [ObservableProperty]
    public partial string? TimesheetNote { get; private set; }

    partial void OnSelectedDateChanged(DateTime? value)
    {
        if (value is { } d && DateOnly.FromDateTime(d) > CurrentDay)
        {
            SelectedDate = CurrentDay.ToDateTime(TimeOnly.MinValue); // nothing is registered in the future
            return;
        }

        _ = LoadTimelineAsync();
    }

    [RelayCommand]
    private void PreviousDay() => SelectedDate = SelectedDay.AddDays(-1).ToDateTime(TimeOnly.MinValue);

    private bool CanGoForward() => !IsToday;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void NextDay() => SelectedDate = SelectedDay.AddDays(1).ToDateTime(TimeOnly.MinValue);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private void GoToToday() => SelectedDate = CurrentDay.ToDateTime(TimeOnly.MinValue);

    private async Task LoadTimelineAsync()
    {
        var day = SelectedDay;
        IReadOnlyList<TimelineEvent> events = [];
        WorkTrace.Core.Reporting.DayRegistrations? registrations = null;
        if (!await RunAsync(
            async () =>
            {
                events = await _checkIns.GetDayTimelineAsync(day, TimeZoneInfo.Local);
                registrations = await _timesheets.GetDayAsync(day);
            },
            "Could not load the day's registrations."))
        {
            return;
        }

        if (day != SelectedDay || registrations is null)
        {
            return; // another day was chosen meanwhile
        }

        ShowSummary(registrations.Summary);
        Registrations.Clear();
        foreach (var entry in registrations.Entries)
        {
            Registrations.Add(new RegistrationItemViewModel(entry));
        }

        HasNoRegistrations = Registrations.Count == 0;
        RegistrationTotal = $"{Registrations.Count} registration{(Registrations.Count == 1 ? "" : "s")} · {F(registrations.RegisteredMinutes)} work";
        TimesheetNote = registrations.HasCorrections
            ? $"Timesheet total for this day: {F(registrations.TimesheetMinutes)} (includes your corrections there)"
            : null;

        Today.Clear();
        foreach (var item in events.Reverse())
        {
            Today.Add(new TimelineItemViewModel(item));
        }

        var checkIns = events.Count(e => e.Kind == TimelineEventKind.CheckIn);
        var missed = events.Count(e => e.Kind is TimelineEventKind.MissedCheckIn or TimelineEventKind.DismissedCheckIn or TimelineEventKind.SuppressedCheckIn);
        TodaySummary = events.Count == 0
            ? "Nothing recorded on this day."
            : $"{checkIns} check-in{(checkIns == 1 ? string.Empty : "s")}" + (missed > 0 ? $" · {missed} without an answer" : string.Empty);
    }

    private static string F(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

    private void ShowSummary(WorkTrace.Core.Reporting.DaySummary sum)
    {
        DailySummary.Clear();
        DailySummary.Add(new("Tracked", F(sum.TrackedMinutes), "Time WorkTrace was tracking (not paused, asleep or closed)"));
        DailySummary.Add(new("On tasks", "≈ " + F(sum.TaskMinutes), "Registered tasks: confirmed plus estimated from check-ins"));
        if (sum.ConfirmedTaskMinutes > 0)
        {
            DailySummary.Add(new("confirmed", F(sum.ConfirmedTaskMinutes), "Entered manually", IsDetail: true));
        }

        DailySummary.Add(new("from check-ins", "≈ " + F(sum.EstimatedTaskMinutes), "Each check-in starts its task; it runs until the next check-in or stop", IsDetail: true));
        DailySummary.Add(new("Other activity", "≈ " + F(sum.OtherMinutes), "Stakeholder-only or note-only check-ins"));
        DailySummary.Add(new("Breaks", F(sum.BreakMinutes), "Time registered as not work"));
        DailySummary.Add(new("Locked", F(sum.LockedMinutes), "Screen locked while tracking (idle time is not recorded separately)"));
        DailySummary.Add(new("Unaccounted", F(sum.UnaccountedMinutes), "Tracking on, but no check-in says what it was (e.g. after a missed check-in)"));
    }

    private async Task LoadCurrentTaskAsync()
    {
        IReadOnlyList<TaskSummary> tasks = [];
        ObservationSummary? last = null;
        if (!await RunAsync(
            async () =>
            {
                tasks = await _tasks.SearchAsync(new TaskQuery(Limit: 500));
                last = await _checkIns.GetLastObservationAsync();
            },
            "Could not load tasks."))
        {
            return;
        }

        _loadingCurrent = true;
        try
        {
            CurrentTasks.Clear();
            foreach (var t in tasks.Where(t => !t.IsClosed).OrderByDescending(t => t.LastUsedAtUtc ?? DateTime.MinValue).ThenBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                CurrentTasks.Add(new CurrentTaskOption(t.Id, CurrentTaskOption.Format(t), t.ClientName, t.DeadlineState != DeadlineState.None));
            }

            var current = CurrentTasks.FirstOrDefault(o => o.Id == last?.TaskId);
            CurrentTaskId = current?.Id ?? PickerOption.NoneKey;
            _recordedTaskId = current?.Id;
            CurrentTaskText = current?.Display ?? string.Empty;
        }
        finally
        {
            _loadingCurrent = false;
        }
    }

    /// <summary>
    /// Records a check-in for the chosen task when it differs from the current one. Called when the lookup closes or
    /// loses focus, so browsing the list with the arrow keys does not record anything.
    /// </summary>
    [RelayCommand]
    private async Task CommitCurrentTaskAsync()
    {
        if (_loadingCurrent || CurrentTaskId == PickerOption.NoneKey || CurrentTaskId == _recordedTaskId)
        {
            return;
        }

        var taskId = CurrentTaskId;
        _recordedTaskId = taskId;
        if (await RunAsync(() => _checkIns.RecordAsync(new CheckInRequest(null, taskId, null)), "Could not record the current task."))
        {
            Notifications.Show("Current task recorded.", NotificationKind.Success);
            await LoadTimelineAsync();
            await LoadCurrentTaskAsync();
        }
        else
        {
            _recordedTaskId = null;
        }
    }

    [RelayCommand]
    private void NewTask() => _navigation.Navigate(AppSection.Tasks, new CreateNewRequest());

    [RelayCommand]
    private void NewClient() => _navigation.Navigate(AppSection.Clients, new CreateNewRequest());

    [RelayCommand]
    private void Navigate(AppSection section) => _navigation.Navigate(section);

    [RelayCommand]
    private void OpenDataFolder()
    {
        if (Status is not null)
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Status.DataDirectory}\"") { UseShellExecute = true });
        }
    }
}

/// <param name="IsDetail">Indented sub-line (part of the line above).</param>
/// <summary>One registration row in the Overview day list.</summary>
public sealed class RegistrationItemViewModel(WorkTrace.Core.Reporting.RegistrationEntry entry)
{
    public WorkTrace.Core.Reporting.RegistrationEntry Entry { get; } = entry;

    public string Time { get; } =
        $"{TimeZoneInfo.ConvertTimeFromUtc(entry.StartUtc, TimeZoneInfo.Local):HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(entry.EndUtc, TimeZoneInfo.Local):HH:mm}";

    public string Duration { get; } = $"{(entry.IsConfirmed ? "" : "≈ ")}{(int)entry.Duration.TotalHours}:{entry.Duration.Minutes:00}";

    public string Task => Entry.Reference is { Length: > 0 } r ? $"{r} · {Entry.Task}" : Entry.Task ?? "";

    public string Stakeholder => Entry.Stakeholder ?? (Entry.IsBreak ? "Not work" : "No stakeholder");

    public string StakeholderColor => Entry.StakeholderColor ?? "#7A7F87";

    public string KindText => Entry.IsBreak ? "Break" : Entry.IsConfirmed ? "Confirmed" : "From check-in (estimated)";
}

public sealed record SummaryRow(string Label, string Value, string? Hint = null, bool IsDetail = false);

/// <param name="HasDeadlineWarning">Deadline within the warning period or overdue: the lookup shows a bell.</param>
public sealed record CurrentTaskOption(int Id, string Display, string Stakeholder, bool HasDeadlineWarning)
{
    public static string Format(TaskSummary t) => string.IsNullOrWhiteSpace(t.ExternalId) ? t.Title : $"{t.ExternalId} · {t.Title}";

    public override string ToString() => Display;
}
