using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.App.Views;

namespace WorkTrace.App.Shell;

/// <summary>Creates, shows and re-activates application windows. Must be used on the UI thread.</summary>
internal sealed class WindowManager(IServiceProvider services)
{
    private MainWindow? _mainWindow;

    public void ShowMainWindow() => ShowMainWindow(null);

    /// <summary>Shows the main window, optionally switching to <paramref name="section"/>.</summary>
    public void ShowMainWindow(AppSection? section, object? parameter = null)
    {
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
        _mainWindow = null;
        foreach (var window in Application.Current.Windows.OfType<Window>().ToList())
        {
            window.Close();
        }
    }
}
