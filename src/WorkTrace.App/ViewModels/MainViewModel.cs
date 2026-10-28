using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Services;

namespace WorkTrace.App.ViewModels;

public sealed partial class MainViewModel(IAppStatusService statusService, ILogger<MainViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    public partial AppStatus? Status { get; private set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    public string Version { get; } = typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    /// <summary>Upcoming features, listed honestly instead of exposing placeholder screens.</summary>
    public ObservableCollection<RoadmapItem> Roadmap { get; } =
    [
        new("Clients & tasks", "Phase 2"),
        new("Check-ins & tray tracking", "Phase 3"),
        new("Activity log & time estimation", "Phase 4"),
        new("Screenshots (opt-in)", "Phase 5"),
        new("Weekly timesheets & export", "Phase 6"),
        new("Settings UI, backup/restore, packaging", "Phase 7"),
    ];

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            ErrorMessage = null;
            Status = await statusService.GetStatusAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to load application status");
            ErrorMessage = "Could not read the database status. See the log for details.";
        }
    }

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
