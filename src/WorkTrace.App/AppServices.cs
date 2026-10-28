using Microsoft.Extensions.DependencyInjection;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
using WorkTrace.App.Tray;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;

namespace WorkTrace.App;

public static class AppServices
{
    /// <summary>Registers shell services, view models and windows. Also used by the UI tests.</summary>
    public static IServiceCollection AddWorkTraceApp(this IServiceCollection services, IAppLifetime lifetime)
    {
        services.AddSingleton(lifetime);
        services.AddSingleton<WindowManager>();
        services.AddSingleton<TrayIconService>();

        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();

        // Tracking: the engine itself is registered by Infrastructure and shows popups through this presenter.
        services.AddSingleton<CheckInPresenter>();
        services.AddSingleton<WorkTrace.Core.Tracking.ICheckInPresenter>(sp => sp.GetRequiredService<CheckInPresenter>());
        services.AddSingleton<TrackingController>();
        services.AddSingleton<GlobalHotkey>();
        services.AddSingleton<SystemEventsBridge>();
        services.AddTransient<CheckInViewModel>();

        // One main window per process: page view models are singletons so state (search text,
        // unsaved edits) survives hiding the window to the tray.
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<TasksViewModel>();
        services.AddSingleton<ClientsViewModel>();
        services.AddSingleton<ListsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<MainWindow>();

        return services;
    }
}
