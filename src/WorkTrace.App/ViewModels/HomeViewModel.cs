using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Services;

namespace WorkTrace.App.ViewModels;

/// <summary>Overview page: storage status, quick actions and what's coming next.</summary>
public sealed partial class HomeViewModel(
    IAppStatusService statusService,
    INavigationService navigation,
    INotificationService notifications,
    ILogger<HomeViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    [ObservableProperty]
    public partial AppStatus? Status { get; private set; }

    public string Version { get; } = typeof(HomeViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    public bool HasUnsavedChanges => false;

    public IReadOnlyList<RoadmapItem> Roadmap { get; } =
    [
        new("Check-ins & tray tracking", "Phase 3"),
        new("Activity log & time estimation", "Phase 4"),
        new("Screenshots (opt-in)", "Phase 5"),
        new("Weekly timesheets & export", "Phase 6"),
        new("Settings, backup & installer", "Phase 7"),
    ];

    public async Task ActivateAsync(object? parameter) =>
        await RunAsync(async () => Status = await statusService.GetStatusAsync(), "Could not read the database status.");

    [RelayCommand]
    private void NewTask() => navigation.Navigate(AppSection.Tasks, new CreateNewRequest());

    [RelayCommand]
    private void NewClient() => navigation.Navigate(AppSection.Clients, new CreateNewRequest());

    [RelayCommand]
    private void Navigate(AppSection section) => navigation.Navigate(section);

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
