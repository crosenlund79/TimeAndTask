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
    private readonly WorkTrace.Core.Reporting.IRegistrationService _registrations;
    private readonly IRegistrationEditor _editor;
    private readonly IDialogService _dialogs;
    private readonly ITimer _refreshTimer;
    private bool _loadingCurrent;
    private int? _recordedTaskId;

    public HomeViewModel(
        IAppStatusService statusService,
        ICheckInService checkIns,
        ITaskService tasks,
        WorkTrace.Core.Reporting.ITimesheetService timesheets,
        WorkTrace.Core.Reporting.IRegistrationService registrations,
        IRegistrationEditor editor,
        IDialogService dialogs,
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
        _registrations = registrations;
        _editor = editor;
        _dialogs = dialogs;
        Tracking = tracking;

        // Keep "Today" current: after every check-in from any UI (popup, shortcut, this page) …
        checkIns.Recorded += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _ = LoadTimelineAsync();
            _ = LoadCurrentTaskAsync();
        });

        // … and once a minute, so the active interval's running time stays current.
        _refreshTimer = timeProvider.CreateTimer(
            _ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (IsToday && !IsBusy)
                {
                    _ = LoadTimelineAsync();
                }
            }),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
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
        if (parameter is DateOnly day && day != SelectedDay)
        {
            SelectedDate = day.ToDateTime(TimeOnly.MinValue); // opened from the Timesheet: show that day's registrations
        }

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

    /// <summary>Daily task history: every distinct task chosen or active on the selected day (informational).</summary>
    public ObservableCollection<WorkTrace.Core.Reporting.DayTask> DayTasks { get; } = [];

    [ObservableProperty]
    public partial bool HasDayTasks { get; private set; }

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

    /// <summary>Edit a registration, or classify an unclassified interval (assign it to a task or stakeholder).</summary>
    [RelayCommand]
    private async Task EditRegistrationAsync(RegistrationItemViewModel? item)
    {
        if (item is null || item.IsPaused || item.IsActive && item.CanClassify)
        {
            return;
        }

        if (await _editor.EditAsync(item.Entry))
        {
            await LoadTimelineAsync();
        }
    }

    /// <summary>Deletes a registration: its time becomes unclassified again (visible, classifiable later).</summary>
    [RelayCommand]
    private async Task DeleteRegistrationAsync(RegistrationItemViewModel? item)
    {
        if (item is null || !item.CanDelete
            || !_dialogs.Confirm(
                "Delete registration",
                $"Delete the registration {item.Time} ({item.Duration})? The time becomes unclassified; you can classify it again later.",
                "Delete",
                destructive: true))
        {
            return;
        }

        var entry = item.Entry;
        if (await RunAsync(() => _registrations.DeleteAsync(entry.StartUtc, entry.EndUtc), "Could not delete the registration."))
        {
            await LoadTimelineAsync();
        }
    }

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
        DayTasks.Clear();
        foreach (var task in registrations.Tasks)
        {
            DayTasks.Add(task);
        }

        HasDayTasks = DayTasks.Count > 0;
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

        DailySummary.Add(new("from check-ins", "≈ " + F(sum.EstimatedTaskMinutes), "The chosen task runs until another task is chosen, a pause or exit", IsDetail: true));
        DailySummary.Add(new("Other activity", "≈ " + F(sum.OtherMinutes), "Time on a stakeholder without a task"));
        DailySummary.Add(new("Breaks", F(sum.BreakMinutes), "Time registered as not work"));
        DailySummary.Add(new("Locked", F(sum.LockedMinutes), "Screen locked while tracking (idle time is not recorded separately)"));
        DailySummary.Add(new("Unaccounted", F(sum.UnaccountedMinutes), "Tracking on, no task chosen yet (e.g. since start-up or a resume); the next task chosen gets it"));
    }

    private async Task LoadCurrentTaskAsync()
    {
        IReadOnlyList<TaskSummary> tasks = [];
        WorkTrace.Core.Reporting.ActiveInterval? active = null;
        if (!await RunAsync(
            async () =>
            {
                tasks = await _tasks.SearchAsync(new TaskQuery(Limit: 500));
                active = await _registrations.GetActiveAsync(_timeProvider.GetUtcNow().UtcDateTime);
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

            // The task of the active interval (the same estimate as the registrations), not merely the last check-in:
            // after a restart or a pause the time is unclassified, and choosing that task again must register it.
            var current = CurrentTasks.FirstOrDefault(o => o.Id == active?.TaskId);
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

    /// <summary>"09:14–10:29"; the running interval has no end yet: "09:14–now".</summary>
    public string Time { get; } = entry.IsActive
        ? $"{TimeZoneInfo.ConvertTimeFromUtc(entry.StartUtc, TimeZoneInfo.Local):HH:mm}–now"
        : $"{TimeZoneInfo.ConvertTimeFromUtc(entry.StartUtc, TimeZoneInfo.Local):HH:mm}–{TimeZoneInfo.ConvertTimeFromUtc(entry.EndUtc, TimeZoneInfo.Local):HH:mm}";

    /// <summary>The interval running now (shown with an "Active" badge, never as a completed registration).</summary>
    public bool IsActive => Entry.IsActive;

    public bool IsLocked => Entry.Kind == WorkTrace.Core.Reporting.RegistrationKind.Locked;

    /// <summary>Unclassified or locked time: shown muted, with Classify.</summary>
    public bool CanClassify => IsUnclassified || IsLocked;

    /// <summary>
    /// The check-ins behind the row: "chosen 09:14 (counted from 08:14)", "chosen 09:14 · again 11:02". Explains a row that
    /// starts before its selection (the unclassified time since start-up or a resume was classified by it) and selections
    /// of the same task that continued it instead of starting a new row.
    /// </summary>
    public string? SelectionText { get; } = entry.Selections.Count == 0 ? null : string.Join(" · ", entry.Selections.Select((s, i) =>
        (i == 0 ? "chosen " : "again ") + TimeZoneInfo.ConvertTimeFromUtc(s, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
        + (i == 0 && s - entry.StartUtc >= TimeSpan.FromMinutes(1) ? $" (counted from {TimeZoneInfo.ConvertTimeFromUtc(entry.StartUtc, TimeZoneInfo.Local):HH:mm})" : "")));

    public string Duration { get; } = $"{(entry.IsConfirmed ? "" : "≈ ")}{(int)entry.Duration.TotalHours}:{entry.Duration.Minutes:00}";

    public string Task => Entry.Reference is { Length: > 0 } r ? $"{r} · {Entry.Task}" : Entry.Task ?? "";

    public string Stakeholder => Entry.Stakeholder ?? (Entry.IsBreak ? "Not work" : "No stakeholder");

    public string StakeholderColor => Entry.StakeholderColor ?? "#7A7F87";

    public bool IsUnclassified => Entry.Kind == WorkTrace.Core.Reporting.RegistrationKind.Unclassified;

    public bool IsPaused => Entry.Kind == WorkTrace.Core.Reporting.RegistrationKind.Paused;

    /// <summary>Work and break rows can be edited and deleted; unclassified and locked rows can be classified.</summary>
    public bool CanDelete => !CanClassify && !IsPaused;

    public bool CanEdit => CanDelete;

    public string KindText => Entry.Kind switch
    {
        WorkTrace.Core.Reporting.RegistrationKind.Unclassified => "Unclassified: not yet assigned to a task (Classify to assign it)",
        WorkTrace.Core.Reporting.RegistrationKind.Paused => "Registration paused: not working time",
        WorkTrace.Core.Reporting.RegistrationKind.Locked => "Screen locked: not counted (Classify if it was work); the task continues after it",
        _ => Entry.IsBreak ? "Break" : Entry.IsConfirmed ? "Confirmed" : "From check-in (estimated)",
    };
}

public sealed record SummaryRow(string Label, string Value, string? Hint = null, bool IsDetail = false);

/// <param name="HasDeadlineWarning">Deadline within the warning period or overdue: the lookup shows a bell.</param>
public sealed record CurrentTaskOption(int Id, string Display, string Stakeholder, bool HasDeadlineWarning)
{
    public static string Format(TaskSummary t) => string.IsNullOrWhiteSpace(t.ExternalId) ? t.Title : $"{t.ExternalId} · {t.Title}";

    public override string ToString() => Display;
}
