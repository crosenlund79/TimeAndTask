using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
using WorkTrace.App.Tray;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure;
using WorkTrace.Infrastructure.Logging;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App;

public partial class App : Application, IAppLifetime
{
    private SingleInstanceGuard? _singleInstance;
    private IHost? _host;
    private ILogger<App>? _logger;
    private bool _exitStarted;

    public bool IsExiting => _exitStarted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = SingleInstanceGuard.TryAcquire();
        if (_singleInstance is null)
        {
            SingleInstanceGuard.SignalRunningInstance();
            Shutdown(0);
            return;
        }

        RegisterGlobalExceptionHandlers();

        try
        {
            var paths = AppPaths.Resolve();
            paths.EnsureDirectories();

            _host = BuildHost(paths, e.Args);
            _logger = _host.Services.GetRequiredService<ILogger<App>>();
            _logger.LogInformation("WorkTrace {Version} starting", AppVersion.Text);

            await _host.StartAsync();

            var firstRun = await _host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
            var settings = await _host.Services.GetRequiredService<ISettingsStore>().LoadAsync();
            ApplyTheme(settings.General.Theme);

            var tray = _host.Services.GetRequiredService<TrayIconService>();
            tray.Initialize();

            StartTracking(_host.Services, tray, settings);

            var windows = _host.Services.GetRequiredService<WindowManager>();
            _singleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(windows.ShowMainWindow));

            // The installer closes WorkTrace this way before an upgrade: a normal, quiet shutdown (no exit reminder).
            _singleInstance.ListenForExit(() => Dispatcher.BeginInvoke(() =>
            {
                _logger?.LogInformation("Closing at the installer's request");
                ShutdownCore(exitCode: 0);
            }));

            if (firstRun || !settings.General.StartMinimized || e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase))
            {
                windows.ShowMainWindow();
            }

            if (firstRun)
            {
                tray.ShowNotification("WorkTrace is running", "WorkTrace lives in the notification area. Double-click the icon to open it.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Startup failed");
            MessageBox.Show(
                $"WorkTrace could not start.\n\n{ex.Message}",
                "WorkTrace",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ShutdownCore(exitCode: 1);
        }
    }

    public void RequestExit() => _ = ExitAsync();

    /// <summary>Exit from the tray: the optional "registered your time?" question first, then a normal shutdown.</summary>
    private async Task ExitAsync()
    {
        try
        {
            if (_host?.Services.GetService<TimeReminderService>() is { } reminders)
            {
                await reminders.AskBeforeExitAsync();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exit reminder failed");
        }

        ShutdownCore(exitCode: 0);
    }

    private void StartTracking(IServiceProvider services, TrayIconService tray, WorkTraceSettings settings)
    {
        var presenter = services.GetRequiredService<CheckInPresenter>();
        var hotkey = services.GetRequiredService<GlobalHotkey>();
        hotkey.Pressed += (_, _) => presenter.ShowManual();
        if (!hotkey.Register(settings.General.GlobalShortcut))
        {
            tray.ShowNotification("Shortcut not available", $"{settings.General.GlobalShortcut} is used by another application. Choose another one in Settings.");
        }

        var store = services.GetRequiredService<ISettingsStore>();
        store.Changed += (_, changed) => Dispatcher.BeginInvoke(() => OnSettingsChanged(changed, hotkey, tray));

        services.GetRequiredService<SystemEventsBridge>().Start();
        _ = StartEngineAsync(services.GetRequiredService<TrackingEngine>());

        services.GetRequiredService<ScreenshotCaptureService>().Start();
        services.GetRequiredService<BackupScheduler>().Start();

        var reminders = services.GetRequiredService<TimeReminderService>();
        reminders.Start();
        _ = reminders.AskAfterShutdownAsync();
    }

    private async Task StartEngineAsync(TrackingEngine engine)
    {
        try
        {
            await engine.StartAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "Tracking could not start");
            MessageBox.Show($"Tracking could not start.\n\n{ex.Message}", "WorkTrace", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OnSettingsChanged(WorkTraceSettings settings, GlobalHotkey hotkey, TrayIconService tray)
    {
        ApplyTheme(settings.General.Theme);
        if (!string.Equals(hotkey.Current, settings.General.GlobalShortcut, StringComparison.OrdinalIgnoreCase)
            && !hotkey.Register(settings.General.GlobalShortcut))
        {
            tray.ShowNotification("Shortcut not available", $"{settings.General.GlobalShortcut} is used by another application or is not valid.");
        }

        tray.UpdateShortcut(settings.General.GlobalShortcut);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off or shutting down: stop cleanly so logs are flushed (and, from Phase 3,
        // the open tracking segment is closed with an accurate end time).
        _logger?.LogInformation("Windows session ending ({Reason})", e.ReasonSessionEnding);
        base.OnSessionEnding(e);

        // Never delays or cancels the shutdown: an outstanding time registration is asked about at the next start.
        _host?.Services.GetService<TimeReminderService>()?.OnSessionEnding();
        ShutdownCore(exitCode: 0);
    }

    private IHost BuildHost(AppPaths paths, string[] args)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddWorkTraceFileLogging(paths.LogsDirectory);
#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddWorkTraceInfrastructure(paths);
        builder.Services.AddWorkTraceApp(this);

        return builder.Build();
    }

    private static void ApplyTheme(ThemePreference theme)
    {
        Current.ThemeMode = theme switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
    }

    /// <summary>
    /// Synchronous on purpose: it also runs from <see cref="OnSessionEnding"/>, where Windows expects
    /// cleanup to finish before the handler returns. The host is stopped on a pool thread so no
    /// continuation needs the (blocked) UI thread.
    /// </summary>
    private void ShutdownCore(int exitCode)
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        _logger?.LogInformation("WorkTrace shutting down");

        try
        {
            // Stop tracking first so the segment ends now (not when the window happens to close).
            if (_host?.Services.GetService<TrackingEngine>() is { } engine)
            {
                Task.Run(engine.StopAsync).Wait(TimeSpan.FromSeconds(5));
            }

            _host?.Services.GetService<SystemEventsBridge>()?.Dispose();
            _host?.Services.GetService<TimeReminderService>()?.Dispose();
            _host?.Services.GetService<ScreenshotCaptureService>()?.Dispose();
            _host?.Services.GetService<BackupScheduler>()?.Dispose();
            _host?.Services.GetService<GlobalHotkey>()?.Dispose();
            _host?.Services.GetService<WindowManager>()?.CloseAll();
            _host?.Services.GetService<TrayIconService>()?.Dispose();

            if (_host is { } host)
            {
                Task.Run(async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await host.StopAsync(timeout.Token).ConfigureAwait(false);
                }).Wait(TimeSpan.FromSeconds(6));
                host.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error during shutdown");
        }
        finally
        {
            _singleInstance?.Dispose();
            _singleInstance = null;
            Shutdown(exitCode);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _logger?.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger?.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        MessageBox.Show(
            $"An unexpected error occurred. WorkTrace will keep running.\n\n{e.Exception.Message}\n\nDetails were written to the log file.",
            "WorkTrace",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Handled = true;
    }
}
