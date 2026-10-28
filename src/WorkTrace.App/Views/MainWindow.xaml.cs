using System.ComponentModel;
using System.Windows;
using WorkTrace.App.Shell;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Views;

public partial class MainWindow : Window
{
    private readonly IAppLifetime _lifetime;
    private readonly ISettingsStore _settings;

    public MainWindow(MainViewModel viewModel, IAppLifetime lifetime, ISettingsStore settings)
    {
        _lifetime = lifetime;
        _settings = settings;
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_lifetime.IsExiting)
        {
            return;
        }

        e.Cancel = true;
        if (_settings.Current.General.CloseToTray)
        {
            // Keep the window instance (and any unsaved edits) alive: the app continues in the tray.
            Hide();
        }
        else
        {
            _lifetime.RequestExit();
        }
    }
}
