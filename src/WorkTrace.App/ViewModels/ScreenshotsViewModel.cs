using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Common;
using WorkTrace.Core.Screenshots;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.ViewModels;

public sealed class ScreenshotItemViewModel(ScreenshotItem item)
{
    public ScreenshotItem Item { get; } = item;

    public string Time { get; } = TimeZoneInfo.ConvertTimeFromUtc(item.CapturedAtUtc, TimeZoneInfo.Local).ToString("HH:mm", CultureInfo.CurrentCulture);

    public string Context => Item.Context ?? "No check-in yet";

    public string FullPath => Item.FullPath;
}

/// <summary>Automatic screenshots by day: thumbnails with the time and the task you were on, open, delete.</summary>
public sealed partial class ScreenshotsViewModel(
    IScreenshotService screenshots,
    ISettingsStore settings,
    IDialogService dialogs,
    IFileDialogService files,
    INavigationService navigation,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<ScreenshotsViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    public bool HasUnsavedChanges => false;

    public ObservableCollection<ScreenshotItemViewModel> Items { get; } = [];

    public IReadOnlyList<ScreenshotItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectionCount { get; private set; }

    [ObservableProperty]
    public partial DateOnly Day { get; private set; }

    [ObservableProperty]
    public partial string DayTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string? Status { get; private set; }

    [ObservableProperty]
    public partial bool IsOff { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local));

    public Task ActivateAsync(object? parameter) => LoadAsync(Day == default ? Today : Day);

    public void SetSelection(IEnumerable<ScreenshotItemViewModel> items)
    {
        SelectedItems = [.. items];
        SelectionCount = SelectedItems.Count;
    }

    public async Task LoadAsync(DateOnly day)
    {
        Day = day;
        DayTitle = day == Today ? $"Today, {day.ToString("d MMMM", CultureInfo.CurrentCulture)}" : day.ToString("dddd d MMMM yyyy", CultureInfo.CurrentCulture);
        await RunAsync(async () =>
        {
            var items = await screenshots.GetDayAsync(day);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(new ScreenshotItemViewModel(item));
            }

            var (count, bytes) = await screenshots.GetStorageAsync();
            var s = settings.Current.Screenshots;
            IsOff = s.Mode == ScreenshotMode.Off;
            Status = $"{Items.Count} today · {count} in total, {bytes / (1024.0 * 1024):0} MB · kept {s.RetentionDays} days"
                + (IsOff ? "" : $" · every {s.CaptureIntervalMinutes} min, {(s.Mode == ScreenshotMode.AllScreens ? "all screens" : "main screen")}");
        }, "Could not load the screenshots.");
        SetSelection([]);
    }

    [RelayCommand]
    private Task PreviousDayAsync() => MoveAsync(forward: false);

    [RelayCommand]
    private Task NextDayAsync() => MoveAsync(forward: true);

    [RelayCommand]
    private Task TodayAsync() => LoadAsync(Today);

    private async Task MoveAsync(bool forward)
    {
        DateOnly? next = null;
        await RunAsync(async () => next = await screenshots.GetAdjacentDayAsync(Day, forward), "Could not load the screenshots.");
        await LoadAsync(next ?? (forward ? (Day < Today ? Today : Day) : Day.AddDays(-1)));
    }

    [RelayCommand]
    private void Open(ScreenshotItemViewModel item) => files.OpenFile(item.FullPath);

    [RelayCommand]
    private void OpenSettings() => navigation.Navigate(Shell.AppSection.Settings);

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        var items = SelectedItems.Count > 0 ? SelectedItems : [];
        if (items.Count == 0)
        {
            return;
        }

        if (dialogs.Confirm(items.Count == 1 ? "Delete screenshot" : $"Delete {items.Count} screenshots",
                $"Delete {(items.Count == 1 ? "this screenshot" : $"{items.Count} screenshots")} from this PC? This cannot be undone.", "Delete", destructive: true)
            && await RunAsync(() => screenshots.DeleteAsync([.. items.Select(i => i.Item.Id)]), "Could not delete the screenshots."))
        {
            await LoadAsync(Day);
        }
    }
}
