using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
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

    public HomeViewModel(
        IAppStatusService statusService,
        ICheckInService checkIns,
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
        Tracking = tracking;

        // Keep "Today" current while the page is open.
        tracking.CheckInRecorded += (_, _) => _ = LoadTimelineAsync();
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

    [ObservableProperty]
    public partial string? TodaySummary { get; private set; }

    [ObservableProperty]
    public partial AppStatus? Status { get; private set; }

    public string Version { get; } = typeof(HomeViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool HasUnsavedChanges => false;

    public IReadOnlyList<RoadmapItem> Roadmap { get; } =
    [
        new("Activity log & time estimation", "Phase 4"),
        new("Screenshots (opt-in)", "Phase 5"),
        new("Weekly timesheets & export", "Phase 6"),
        new("Settings, backup & installer", "Phase 7"),
    ];

    public async Task ActivateAsync(object? parameter)
    {
        await RunAsync(async () => Status = await _statusService.GetStatusAsync(), "Could not read the database status.");
        await LoadTimelineAsync();
    }

    private async Task LoadTimelineAsync()
    {
        IReadOnlyList<TimelineEvent> events = [];
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcNow().UtcDateTime, TimeZoneInfo.Local));
        if (!await RunAsync(async () => events = await _checkIns.GetDayTimelineAsync(today, TimeZoneInfo.Local), "Could not load today's activity."))
        {
            return;
        }

        Today.Clear();
        foreach (var item in events.Reverse())
        {
            Today.Add(new TimelineItemViewModel(item));
        }

        var checkIns = events.Count(e => e.Kind == TimelineEventKind.CheckIn);
        var missed = events.Count(e => e.Kind is TimelineEventKind.MissedCheckIn or TimelineEventKind.DismissedCheckIn or TimelineEventKind.SuppressedCheckIn);
        TodaySummary = events.Count == 0
            ? "Nothing recorded today yet."
            : $"{checkIns} check-in{(checkIns == 1 ? string.Empty : "s")}" + (missed > 0 ? $" · {missed} without an answer" : string.Empty);
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

public sealed record RoadmapItem(string Feature, string Phase);
