using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// Settings page. Every change is validated and saved immediately (no Save button); the tracking
/// engine, hotkey and theme react to the settings store's Changed event.
/// </summary>
public sealed partial class SettingsViewModel(
    ISettingsStore settings,
    TrackingController tracking,
    TimeReminderService reminders,
    INotificationService notifications,
    ILogger<SettingsViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    private bool _loading;

    public TrackingController Tracking { get; } = tracking;

    public bool HasUnsavedChanges => false;

    /// <summary>Key of the "Custom…" interval entry.</summary>
    public const int CustomIntervalKey = -1;

    public static IReadOnlyList<int> PresetIntervals { get; } = [1, 5, 10, 15, 30, 60];

    public IReadOnlyList<PickerOption> IntervalOptions { get; } =
    [
        .. PresetIntervals.Select(m => new PickerOption(m, m switch { 1 => "Every minute", 60 => "Every hour", _ => $"Every {m} minutes" })),
        new PickerOption(CustomIntervalKey, "Custom…"),
    ];

    public IReadOnlyList<PickerOption> AutoDismissOptions { get; } =
    [
        new(30, "After 30 seconds"), new(60, "After 1 minute"), new(120, "After 2 minutes"),
        new(300, "After 5 minutes"), new(600, "After 10 minutes"),
    ];

    public IReadOnlyList<PickerOption> PositionOptions { get; } =
    [
        new((int)PopupPosition.BottomRight, "Bottom right"), new((int)PopupPosition.TopRight, "Top right"), new((int)PopupPosition.Center, "Centre of the screen"),
    ];

    public IReadOnlyList<PickerOption> ThemeOptions { get; } =
    [
        new((int)ThemePreference.System, "Follow Windows"), new((int)ThemePreference.Light, "Light"), new((int)ThemePreference.Dark, "Dark"),
    ];

    [ObservableProperty]
    public partial bool PeriodicPromptsEnabled { get; set; }

    /// <summary>Selected preset (minutes) or <see cref="CustomIntervalKey"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomInterval))]
    public partial int? IntervalChoice { get; set; }

    public bool IsCustomInterval => IntervalChoice == CustomIntervalKey;

    [ObservableProperty]
    public partial string? CustomIntervalText { get; set; }

    [ObservableProperty]
    public partial string? IntervalError { get; private set; }

    [ObservableProperty]
    public partial bool IsFocusedMode { get; set; }

    [ObservableProperty]
    public partial bool IsUnobtrusiveMode { get; set; }

    [ObservableProperty]
    public partial int? PositionKey { get; set; }

    [ObservableProperty]
    public partial int? AutoDismissSeconds { get; set; }

    [ObservableProperty]
    public partial bool ReminderSound { get; set; }

    [ObservableProperty]
    public partial bool RememberLastTask { get; set; }

    [ObservableProperty]
    public partial bool PromptOnStart { get; set; }

    [ObservableProperty]
    public partial bool LockedTimeIsNotWork { get; set; }

    [ObservableProperty]
    public partial string? Shortcut { get; set; }

    [ObservableProperty]
    public partial string? ShortcutError { get; private set; }

    [ObservableProperty]
    public partial int? ThemeKey { get; set; }

    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    // ---- Timesheet ----

    public IReadOnlyList<PickerOption> RoundingOptions { get; } =
    [
        new(1, "No rounding"), new(5, "Nearest 5 minutes"), new(10, "Nearest 10 minutes"), new(15, "Nearest quarter hour"), new(30, "Nearest half hour"),
    ];

    /// <summary>Rounding of each day cell (1 = none; 0 is the picker's "none" key, so "no rounding" is stored as 0 but shown as 1).</summary>
    [ObservableProperty]
    public partial int? RoundingKey { get; set; }

    partial void OnRoundingKeyChanged(int? value)
    {
        if (value is { } key)
        {
            Apply(s => s.Reporting.RoundingMinutes = key == 1 ? 0 : key);
        }
    }

    // ---- Board ----

    [ObservableProperty]
    public partial bool AutoAddTasksToBoard { get; set; }

    // ---- Time registration reminders ----

    public IReadOnlyList<PickerOption> ReminderIntervalOptions { get; } =
    [
        new(15, "Every 15 minutes"), new(30, "Every 30 minutes"), new(60, "Every hour"), new(90, "Every 90 minutes"), new(120, "Every 2 hours"),
    ];

    [ObservableProperty]
    public partial bool RemindersEnabled { get; set; }

    [ObservableProperty]
    public partial string? ReminderStart { get; set; }

    [ObservableProperty]
    public partial string? ReminderEnd { get; set; }

    [ObservableProperty]
    public partial string? ReminderTimeError { get; private set; }

    [ObservableProperty]
    public partial int? ReminderIntervalMinutes { get; set; }

    [ObservableProperty]
    public partial bool ReminderPlaySound { get; set; }

    [ObservableProperty]
    public partial bool ShutdownReminder { get; set; }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private Task TestReminderAsync() => reminders.ShowAsync(test: true);

    public Task ActivateAsync(object? parameter)
    {
        _loading = true;
        var s = settings.Current;
        PeriodicPromptsEnabled = s.Tracking.PeriodicPromptsEnabled;
        var interval = s.Tracking.CheckInIntervalMinutes;
        IntervalChoice = PresetIntervals.Contains(interval) ? interval : CustomIntervalKey;
        CustomIntervalText = interval.ToString(System.Globalization.CultureInfo.CurrentCulture);
        IntervalError = null;
        IsFocusedMode = s.Tracking.PopupMode == PopupMode.Focused;
        IsUnobtrusiveMode = s.Tracking.PopupMode == PopupMode.Unobtrusive;
        PositionKey = (int)s.Tracking.PopupPosition;
        AutoDismissSeconds = s.Tracking.AutoDismissSeconds;
        ReminderSound = s.Tracking.ReminderSound;
        RememberLastTask = s.Tracking.RememberLastTask;
        PromptOnStart = s.Tracking.PromptOnStart;
        LockedTimeIsNotWork = s.Tracking.LockedTimeHandling == LockedTimeHandling.NotWorking;
        Shortcut = s.General.GlobalShortcut;
        ShortcutError = null;
        ThemeKey = (int)s.General.Theme;
        StartMinimized = s.General.StartMinimized;
        CloseToTray = s.General.CloseToTray;
        AutoAddTasksToBoard = s.Board.AutoAddTasks;
        RoundingKey = s.Reporting.RoundingMinutes == 0 ? 1 : s.Reporting.RoundingMinutes;
        RemindersEnabled = s.Reminders.Enabled;
        ReminderStart = s.Reminders.Start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        ReminderEnd = s.Reminders.End.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        ReminderTimeError = null;
        ReminderIntervalMinutes = s.Reminders.IntervalMinutes;
        ReminderPlaySound = s.Reminders.PlaySound;
        ShutdownReminder = s.Reminders.OnShutdown;
        _loading = false;
        return Task.CompletedTask;
    }

    partial void OnPeriodicPromptsEnabledChanged(bool value) => Apply(s => s.Tracking.PeriodicPromptsEnabled = value);

    partial void OnIntervalChoiceChanged(int? value)
    {
        IntervalError = null;
        if (value is { } minutes && minutes != CustomIntervalKey)
        {
            CustomIntervalText = minutes.ToString(System.Globalization.CultureInfo.CurrentCulture);
            ApplyInterval(minutes);
        }
    }

    /// <summary>Committed when the custom box loses focus or Enter is pressed (not on every keystroke).</summary>
    partial void OnCustomIntervalTextChanged(string? value)
    {
        if (_loading || !IsCustomInterval)
        {
            return;
        }

        if (!int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.CurrentCulture, out var minutes)
            || minutes < TrackingSettings.MinIntervalMinutes
            || minutes > TrackingSettings.MaxIntervalMinutes)
        {
            IntervalError = $"Enter whole minutes from {TrackingSettings.MinIntervalMinutes} to {TrackingSettings.MaxIntervalMinutes}.";
            return;
        }

        IntervalError = null;
        ApplyInterval(minutes);
    }

    private void ApplyInterval(int minutes) =>
        Apply(s =>
        {
            s.Tracking.CheckInIntervalMinutes = minutes;

            // Keep the estimation horizon valid: at least one interval.
            s.Tracking.MaxEstimationHorizonMinutes = Math.Max(s.Tracking.MaxEstimationHorizonMinutes, minutes);
        });

    partial void OnIsFocusedModeChanged(bool value)
    {
        if (value)
        {
            Apply(s => s.Tracking.PopupMode = PopupMode.Focused);
        }
    }

    partial void OnIsUnobtrusiveModeChanged(bool value)
    {
        if (value)
        {
            Apply(s => s.Tracking.PopupMode = PopupMode.Unobtrusive);
        }
    }

    partial void OnPositionKeyChanged(int? value)
    {
        if (value is { } key)
        {
            Apply(s => s.Tracking.PopupPosition = (PopupPosition)key);
        }
    }

    partial void OnAutoDismissSecondsChanged(int? value)
    {
        if (value is { } seconds)
        {
            Apply(s => s.Tracking.AutoDismissSeconds = seconds);
        }
    }

    partial void OnReminderSoundChanged(bool value) => Apply(s => s.Tracking.ReminderSound = value);

    partial void OnRememberLastTaskChanged(bool value) => Apply(s => s.Tracking.RememberLastTask = value);

    partial void OnPromptOnStartChanged(bool value) => Apply(s => s.Tracking.PromptOnStart = value);

    partial void OnLockedTimeIsNotWorkChanged(bool value) =>
        Apply(s => s.Tracking.LockedTimeHandling = value ? LockedTimeHandling.NotWorking : LockedTimeHandling.Unclassified);

    partial void OnThemeKeyChanged(int? value)
    {
        if (value is { } key)
        {
            Apply(s => s.General.Theme = (ThemePreference)key);
        }
    }

    partial void OnAutoAddTasksToBoardChanged(bool value) => Apply(s => s.Board.AutoAddTasks = value);

    partial void OnRemindersEnabledChanged(bool value) => Apply(s => s.Reminders.Enabled = value);

    partial void OnReminderStartChanged(string? value) => ApplyTime(value, (s, t) => s.Reminders.Start = t);

    partial void OnReminderEndChanged(string? value) => ApplyTime(value, (s, t) => s.Reminders.End = t);

    private void ApplyTime(string? text, Action<WorkTraceSettings, TimeOnly> set)
    {
        if (_loading)
        {
            return;
        }

        if (!TimeOnly.TryParse(text?.Trim(), System.Globalization.CultureInfo.CurrentCulture, out var time)
            && !TimeOnly.TryParse(text?.Trim(), System.Globalization.CultureInfo.InvariantCulture, out time))
        {
            ReminderTimeError = "Use a time such as 15:00 or 08:00.";
            return;
        }

        ReminderTimeError = null;
        Apply(s => set(s, time));
    }

    partial void OnReminderIntervalMinutesChanged(int? value)
    {
        if (value is { } minutes)
        {
            Apply(s => s.Reminders.IntervalMinutes = minutes);
        }
    }

    partial void OnReminderPlaySoundChanged(bool value) => Apply(s => s.Reminders.PlaySound = value);

    partial void OnShutdownReminderChanged(bool value) => Apply(s => s.Reminders.OnShutdown = value);

    partial void OnStartMinimizedChanged(bool value) => Apply(s => s.General.StartMinimized = value);

    partial void OnCloseToTrayChanged(bool value) => Apply(s => s.General.CloseToTray = value);

    partial void OnShortcutChanged(string? value)
    {
        if (_loading)
        {
            return;
        }

        if (!GlobalHotkey.TryParse(value, out _, out _))
        {
            ShortcutError = "Use a key with at least one modifier, e.g. Ctrl+Alt+Space or Ctrl+Shift+T.";
            return;
        }

        ShortcutError = null;
        Apply(s => s.General.GlobalShortcut = value!.Trim());
    }

    /// <summary>
    /// Saves one change on top of the latest stored settings. Updates are serialised by the store, so
    /// quick successive changes (and a pause from the tray meanwhile) are all kept.
    /// </summary>
    private void Apply(Action<WorkTraceSettings> change)
    {
        if (!_loading)
        {
            _ = UpdateAsync(change);
        }
    }

    private async Task UpdateAsync(Action<WorkTraceSettings> change)
    {
        try
        {
            await settings.UpdateAsync(change);
        }
        catch (SettingsValidationException ex)
        {
            Notifications.Show(ex.Errors[0], NotificationKind.Warning);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Saving settings failed");
            Notifications.Show("Could not save the setting. See the log for details.", NotificationKind.Error);
        }
    }
}
