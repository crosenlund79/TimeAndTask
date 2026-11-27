using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>One minor task row; ticking the box completes (or reopens) it straight away.</summary>
public sealed partial class MinorTaskItemViewModel : ObservableObject
{
    private readonly Action<MinorTaskItemViewModel, bool> _toggled;
    private bool _loading;

    public MinorTaskItemViewModel(MinorTaskSummary summary, DateTime nowUtc, Action<MinorTaskItemViewModel, bool> toggled)
    {
        Summary = summary;
        _toggled = toggled;
        _loading = true;
        IsCompleted = summary.IsCompleted;
        _loading = false;
        IsOverdue = !summary.IsCompleted && summary.ReminderAtUtc is { } at && at <= nowUtc;
        var when = summary.ReminderAtUtc is { } r
            ? TimeZoneInfo.ConvertTimeFromUtc(r, TimeZoneInfo.Local).ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture)
            : null;
        ReminderText = summary.Recurrence is null ? when
            : summary.ReminderDisabled ? "Reminder off"
            : when is null ? null : $"Next {when}";
        RecurrenceText = summary.Recurrence?.Describe();
    }

    /// <summary>A recurring reminder (personal nudge): Done only acknowledges the current occurrence.</summary>
    public bool IsRecurring => Summary.IsRecurring;

    /// <summary>"Every 1 h · Mon–Fri 08:00–16:00" (tooltip of the recurring icon).</summary>
    public string? RecurrenceText { get; }

    public MinorTaskSummary Summary { get; }

    public int Id => Summary.Id;

    public string Title => Summary.Title;

    public string? Description => Summary.Description;

    public string? Stakeholder => Summary.ClientName;

    public string StakeholderColor => Summary.ClientColor ?? "#8C8576";

    public bool HasStakeholder => Summary.ClientName is not null;

    /// <summary>Local reminder time, e.g. "Tue 10 Nov 09:00"; null without a reminder.</summary>
    public string? ReminderText { get; }

    /// <summary>The reminder time has passed and the task is still open.</summary>
    public bool IsOverdue { get; }

    [ObservableProperty]
    public partial bool IsCompleted { get; set; }

    partial void OnIsCompletedChanged(bool value)
    {
        if (!_loading)
        {
            _toggled(this, value);
        }
    }
}

/// <summary>
/// The Minor Tasks group at the top of the task overview: quick add (title, optional reminder), tick to complete,
/// inline editing, and the completed ones on request. Minor tasks never touch time registration or the Board.
/// </summary>
public sealed partial class MinorTasksViewModel : ViewModelBase
{
    private readonly IMinorTaskService _minorTasks;
    private readonly IClientService _clients;
    private readonly TimeProvider _timeProvider;

    public MinorTasksViewModel(
        IMinorTaskService minorTasks,
        IClientService clients,
        INotificationService notifications,
        TimeProvider timeProvider,
        ILogger<MinorTasksViewModel> logger)
        : base(notifications, logger)
    {
        _minorTasks = minorTasks;
        _clients = clients;
        _timeProvider = timeProvider;
    }

    /// <summary>Raised after any change, so other views (and the reminder service) can refresh.</summary>
    public static event EventHandler? Changed;

    public static void RaiseChanged() => Changed?.Invoke(null, EventArgs.Empty);

    public ObservableCollection<MinorTaskItemViewModel> Active { get; } = [];

    public ObservableCollection<MinorTaskItemViewModel> Completed { get; } = [];

    public ObservableCollection<PickerOption> ClientOptions { get; } = [];

    /// <summary>Quarter-hour choices for reminder times (any time can be typed).</summary>
    public IReadOnlyList<string> QuarterHours { get; } =
        [.. Enumerable.Range(0, 96).Select(i => new TimeOnly(i / 4, i % 4 * 15).ToString("HH:mm", CultureInfo.InvariantCulture))];

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    // ---- Quick add ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string? NewTitle { get; set; }

    [ObservableProperty]
    public partial DateTime? NewReminderDate { get; set; }

    [ObservableProperty]
    public partial string? NewReminderTime { get; set; } = "09:00";

    [ObservableProperty]
    public partial string? AddError { get; private set; }

    // ---- Inline editor ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    public partial MinorTaskItemViewModel? Editing { get; private set; }

    public bool IsEditing => Editing is not null;

    [ObservableProperty]
    public partial string? EditTitle { get; set; }

    [ObservableProperty]
    public partial string? EditDescription { get; set; }

    [ObservableProperty]
    public partial int? EditClientKey { get; set; }

    [ObservableProperty]
    public partial DateTime? EditReminderDate { get; set; }

    [ObservableProperty]
    public partial string? EditReminderTime { get; set; }

    [ObservableProperty]
    public partial string? EditError { get; private set; }

    // ---- Recurring reminder (editor) ----

    /// <summary>Daily key in <see cref="RepeatOptions"/>; the others are interval minutes.</summary>
    public const int DailyKey = -1;

    public IReadOnlyList<PickerOption> RepeatOptions { get; } =
    [
        new(15, "Every 15 minutes"), new(30, "Every 30 minutes"), new(45, "Every 45 minutes"), new(60, "Every hour"), new(90, "Every 1 hour 30 minutes"),
        new(120, "Every 2 hours"), new(180, "Every 3 hours"), new(240, "Every 4 hours"), new(DailyKey, "Once a day at"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOneTimeReminder))]
    public partial bool EditRecurring { get; set; }

    public bool ShowOneTimeReminder => !EditRecurring;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditIsDaily), nameof(EditIsInterval))]
    public partial int? EditRepeatKey { get; set; }

    public bool EditIsDaily => EditRepeatKey == DailyKey;

    public bool EditIsInterval => !EditIsDaily;

    [ObservableProperty]
    public partial string? EditDailyTime { get; set; }

    [ObservableProperty]
    public partial string? EditActiveFrom { get; set; }

    [ObservableProperty]
    public partial string? EditActiveUntil { get; set; }

    /// <summary>Monday … Sunday toggles.</summary>
    public IReadOnlyList<WeekdayToggle> EditDays { get; } =
        [.. Enumerable.Range(0, 7).Select(i => new WeekdayToggle((DayOfWeek)((i + 1) % 7)))];

    [ObservableProperty]
    public partial bool EditReminderDisabled { get; set; }

    /// <summary>"Next reminder: Fri 27 Nov 10:00" for a saved recurring minor task.</summary>
    [ObservableProperty]
    public partial string? EditNextText { get; private set; }

    public async Task LoadAsync()
    {
        IReadOnlyList<MinorTaskSummary> all = [];
        IReadOnlyList<ClientSummary> clients = [];
        if (!await RunAsync(
            async () =>
            {
                all = await _minorTasks.GetAsync(includeCompleted: true);
                clients = await _clients.GetClientsForPickerAsync();
            },
            "Could not load minor tasks."))
        {
            return;
        }

        var now = UtcTime.Now(_timeProvider);
        Active.Clear();
        Completed.Clear();
        foreach (var m in all)
        {
            (m.IsCompleted ? Completed : Active).Add(new MinorTaskItemViewModel(m, now, OnToggled));
        }

        ClientOptions.Clear();
        ClientOptions.Add(new PickerOption(PickerOption.NoneKey, "No stakeholder"));
        foreach (var c in clients)
        {
            ClientOptions.Add(new PickerOption(c.Id, c.Name, c.IsActive));
        }

        var overdue = Active.Count(a => a.IsOverdue);
        Summary = Active.Count == 0 ? "Nothing to remember." : $"{Active.Count} open" + (overdue > 0 ? $" · {overdue} due" : "");
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(NewTitle);

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        AddError = null;
        if (!TryReminder(NewReminderDate, NewReminderTime, out var reminder, out var error))
        {
            AddError = error;
            return;
        }

        try
        {
            await _minorTasks.CreateAsync(new MinorTaskInput(NewTitle, ReminderAtUtc: reminder));
        }
        catch (DomainValidationException ex)
        {
            AddError = ex.Errors.For(nameof(MinorTaskInput.Title)) ?? ex.Message;
            return;
        }

        NewTitle = null;
        NewReminderDate = null;
        NewReminderTime = "09:00";
        await ReloadAsync();
    }

    [RelayCommand]
    private void Edit(MinorTaskItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        Editing = item;
        EditTitle = item.Title;
        EditDescription = item.Description;
        EditClientKey = item.Summary.ClientId ?? PickerOption.NoneKey;
        var local = item.Summary.ReminderAtUtc is { } r ? TimeZoneInfo.ConvertTimeFromUtc(r, TimeZoneInfo.Local) : (DateTime?)null;
        EditReminderDate = local?.Date;
        EditReminderTime = local?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "09:00";
        EditCompleted = item.Summary.IsCompleted;
        var rule = item.Summary.Recurrence ?? MinorTaskRecurrence.Default;
        EditRecurring = item.Summary.IsRecurring;
        EditRepeatKey = rule.Kind == WorkTrace.Core.Entities.MinorTaskRepeat.Daily ? DailyKey
            : RepeatOptions.Any(o => o.Id == rule.IntervalMinutes) ? rule.IntervalMinutes : 60;
        EditDailyTime = Hhmm(rule.DailyAt);
        EditActiveFrom = Hhmm(rule.ActiveFrom);
        EditActiveUntil = Hhmm(rule.ActiveUntil);
        foreach (var day in EditDays)
        {
            day.IsOn = rule.IsActiveDay(day.Day);
        }

        EditReminderDisabled = item.Summary.ReminderDisabled;
        EditNextText = item.IsRecurring ? item.ReminderText : null;
        EditError = null;
    }

    private static string Hhmm(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static bool TryTime(string? text, out TimeOnly time) =>
        TimeOnly.TryParse(text?.Trim(), CultureInfo.InvariantCulture, out time) || TimeOnly.TryParse(text?.Trim(), CultureInfo.CurrentCulture, out time);

    /// <summary>The editor's schedule, or an error for an unreadable time.</summary>
    private bool TryRecurrence(out MinorTaskRecurrence? rule, out string? error)
    {
        rule = null;
        error = null;
        if (!EditRecurring)
        {
            return true;
        }

        if (!TryTime(EditDailyTime, out var daily) || !TryTime(EditActiveFrom, out var from) || !TryTime(EditActiveUntil, out var until))
        {
            error = "Use times such as 08:00.";
            return false;
        }

        var days = EditDays.Where(d => d.IsOn).Aggregate(0, (bits, d) => bits | WorkTrace.Core.Entities.MinorTaskRecurrenceDays.Bit(d.Day));
        rule = EditIsDaily
            ? new MinorTaskRecurrence(WorkTrace.Core.Entities.MinorTaskRepeat.Daily, 60, daily, days, from, until)
            : new MinorTaskRecurrence(WorkTrace.Core.Entities.MinorTaskRepeat.Interval, EditRepeatKey ?? 60, daily, days, from, until);
        return true;
    }

    /// <summary>Opens the editor for a minor task (open or completed), e.g. from its reminder popup.</summary>
    public bool EditById(int id)
    {
        var item = Active.Concat(Completed).FirstOrDefault(i => i.Id == id);
        Edit(item);
        return item is not null;
    }

    /// <summary>Done in the editor: saving marks it done (no more reminders) or reopens it.</summary>
    [ObservableProperty]
    public partial bool EditCompleted { get; set; }

    [RelayCommand]
    private void CancelEdit() => Editing = null;

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (Editing is not { } item)
        {
            return;
        }

        EditError = null;
        if (!TryReminder(EditReminderDate, EditReminderTime, out var reminder, out var error))
        {
            EditError = error;
            return;
        }

        if (!TryRecurrence(out var rule, out error))
        {
            EditError = error;
            return;
        }

        try
        {
            await _minorTasks.UpdateAsync(item.Id, new MinorTaskInput(EditTitle, EditDescription, PickerOption.FromKey(EditClientKey ?? 0), reminder, rule));
            if (rule is not null && EditReminderDisabled != item.Summary.ReminderDisabled)
            {
                await _minorTasks.SetReminderDisabledAsync(item.Id, EditReminderDisabled);
            }

            if (EditCompleted != item.Summary.IsCompleted)
            {
                await _minorTasks.SetCompletedAsync(item.Id, EditCompleted);
            }
        }
        catch (DomainValidationException ex)
        {
            EditError = ex.Errors.For(nameof(MinorTaskInput.Title)) ?? ex.Errors.For(nameof(MinorTaskInput.Description))
                ?? ex.Errors.For(nameof(MinorTaskInput.Recurrence)) ?? ex.Message;
            return;
        }

        Editing = null;
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task DeleteEditingAsync()
    {
        if (Editing is { } item && await RunAsync(() => _minorTasks.DeleteAsync(item.Id), "Could not delete the minor task."))
        {
            Editing = null;
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private void ClearNewReminder() => NewReminderDate = null;

    private void OnToggled(MinorTaskItemViewModel item, bool completed) => _ = ToggleAsync(item, completed);

    private async Task ToggleAsync(MinorTaskItemViewModel item, bool completed)
    {
        if (await RunAsync(() => _minorTasks.SetCompletedAsync(item.Id, completed), "Could not update the minor task."))
        {
            if (Editing?.Id == item.Id)
            {
                Editing = null;
            }

            await ReloadAsync();
        }
    }

    private async Task ReloadAsync()
    {
        await LoadAsync();
        RaiseChanged();
    }

    /// <summary>No date = no reminder. A date without a valid time is an error (never silently midnight).</summary>
    private static bool TryReminder(DateTime? date, string? time, out DateTime? reminderUtc, out string? error)
    {
        reminderUtc = null;
        error = null;
        if (date is not { } day)
        {
            return true;
        }

        if (!TimeOnly.TryParse(time?.Trim(), CultureInfo.InvariantCulture, out var t)
            && !TimeOnly.TryParse(time?.Trim(), CultureInfo.CurrentCulture, out t))
        {
            error = "Use a time such as 09:00.";
            return false;
        }

        var local = DateTime.SpecifyKind(DateOnly.FromDateTime(day).ToDateTime(t), DateTimeKind.Unspecified);
        reminderUtc = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local), DateTimeKind.Utc);
        return true;
    }
}

/// <summary>One weekday checkbox of a recurring reminder's schedule.</summary>
public sealed partial class WeekdayToggle(DayOfWeek day) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Name { get; } = day.ToString()[..3];

    [ObservableProperty]
    public partial bool IsOn { get; set; }
}
