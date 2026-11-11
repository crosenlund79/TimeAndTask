using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Shell;

/// <summary>Creates, shows and re-activates application windows. Must be used on the UI thread.</summary>
internal sealed class WindowManager(IServiceProvider services) : IMiniMode
{
    private MainWindow? _mainWindow;
    private MiniWindow? _miniWindow;

    public bool IsActive => _miniWindow is not null;

    /// <summary>
    /// Mini Mode on: the Today panel in its own small window (same view model, same services); the normal window is
    /// hidden, keeping its position, size and state. Off: the mini window closes and the normal window comes back.
    /// Tracking is never touched.
    /// </summary>
    public void Toggle()
    {
        if (_miniWindow is null)
        {
            EnterMiniMode();
        }
        else
        {
            ExitMiniMode();
            ShowMainWindow();
        }
    }

    private void EnterMiniMode()
    {
        var home = services.GetRequiredService<HomeViewModel>();
        var mini = new MiniWindow(home, services.GetRequiredService<ISettingsStore>().Current.General.MiniWindow);
        mini.RestoreRequested += (_, _) => Toggle();
        mini.PinChanged += (_, _) => SaveMiniPlacement();
        _miniWindow = mini;
        mini.Show();
        mini.Activate();
        _mainWindow?.Hide(); // Hide/Show keeps the normal window's position, size and state
        _ = home.ActivateAsync(null); // make sure the panel shows current data even if the Overview was never opened
    }

    private void ExitMiniMode()
    {
        if (_miniWindow is not { } mini)
        {
            return;
        }

        SaveMiniPlacement();
        _miniWindow = null;
        mini.AllowClose = true;
        mini.Close();
    }

    /// <summary>Remembers the mini window's position, size and pin state in settings.json.</summary>
    private void SaveMiniPlacement()
    {
        if (_miniWindow is not { } mini)
        {
            return;
        }

        // One save at a time, in order: a pin change followed by leaving Mini Mode must end with the latest placement.
        var placement = mini.Placement;
        var previous = PlacementSaved;
        PlacementSaved = SaveAsync(services.GetRequiredService<ISettingsStore>(), placement, previous);
    }

    /// <summary>Completes when the latest placement has been written (tests).</summary>
    internal Task PlacementSaved { get; private set; } = Task.CompletedTask;

    private static async Task SaveAsync(ISettingsStore settings, MiniWindowSettings placement, Task previous)
    {
        await previous;
        try
        {
            var copy = settings.Current.Clone();
            copy.General.MiniWindow = placement;
            await settings.SaveAsync(copy);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or SettingsValidationException)
        {
            // Placement is a convenience: never let it disturb the application.
        }
    }

    public void ShowMainWindow() => ShowMainWindow(null);

    /// <summary>Shows the main window, optionally switching to <paramref name="section"/>.</summary>
    public void ShowMainWindow(AppSection? section, object? parameter = null)
    {
        // Asking for the normal window (tray, notification, navigation) ends Mini Mode.
        ExitMiniMode();
        if (_mainWindow is null)
        {
            _mainWindow = services.GetRequiredService<MainWindow>();
            _mainWindow.Closed += (_, _) => _mainWindow = null;
        }

        if (section is { } target)
        {
            _mainWindow.ViewModel.NavigateTo(target, parameter);
        }
        else if (_mainWindow.ViewModel.SelectedSection is null)
        {
            _mainWindow.ViewModel.NavigateTo(AppSection.Home);
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        // Toggling Topmost is the conventional, policy-respecting way to bring a WPF window forward.
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void CloseAll()
    {
        if (_miniWindow is { } mini)
        {
            mini.AllowClose = true; // the application exits: placement was saved when it last changed mode or pin
            _miniWindow = null;
        }

        _mainWindow = null;
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            window.Close();
        }
    }
}
