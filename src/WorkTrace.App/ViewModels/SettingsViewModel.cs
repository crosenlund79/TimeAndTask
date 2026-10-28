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
    INotificationService notifications,
    ILogger<SettingsViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    private bool _loading;

    public TrackingController Tracking { get; } = tracking;

    public bool HasUnsavedChanges => false;

    public IReadOnlyList<PickerOption> IntervalOptions { get; } =
        [.. new[] { 10, 15, 20, 30, 45, 60, 90, 120 }.Select(m => new PickerOption(m, m < 60 ? $"Every {m} minutes" : m == 60 ? "Every hour" : $"Every {m / 60.0:0.#} hours"))];

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

    [ObservableProperty]
    public partial int? IntervalMinutes { get; set; }

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

    public Task ActivateAsync(object? parameter)
    {
        _loading = true;
        var s = settings.Current;
        PeriodicPromptsEnabled = s.Tracking.PeriodicPromptsEnabled;
        IntervalMinutes = s.Tracking.CheckInIntervalMinutes;
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
        _loading = false;
        return Task.CompletedTask;
    }

    partial void OnPeriodicPromptsEnabledChanged(bool value) => Apply(s => s.Tracking.PeriodicPromptsEnabled = value);

    partial void OnIntervalMinutesChanged(int? value)
    {
        if (value is { } minutes)
        {
            Apply(s =>
            {
                s.Tracking.CheckInIntervalMinutes = minutes;

                // Keep the estimation horizon valid: at least one interval, default two.
                s.Tracking.MaxEstimationHorizonMinutes = Math.Max(s.Tracking.MaxEstimationHorizonMinutes, minutes * 2);
            });
        }
    }

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
