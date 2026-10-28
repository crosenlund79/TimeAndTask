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
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.LoadCommand.Execute(null);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_lifetime.IsExiting)
        {
            return;
        }

        if (_settings.Current.General.CloseToTray)
        {
            // Keep the window instance alive and simply hide it: the app continues in the tray.
            e.Cancel = true;
            Hide();
        }
        else
        {
            e.Cancel = true;
            _lifetime.RequestExit();
        }
    }
}
