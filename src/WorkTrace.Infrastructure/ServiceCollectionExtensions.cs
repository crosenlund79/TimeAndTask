using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkTrace.Core.Board;
using WorkTrace.Core.Management;
using WorkTrace.Core.Services;
using WorkTrace.Infrastructure.Board;
using WorkTrace.Infrastructure.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Settings;
using WorkTrace.Infrastructure.Storage;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkTraceInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);

        // Desktop app: short-lived contexts per operation rather than a scoped context.
        services.AddDbContextFactory<WorkTraceDbContext>(options =>
            options.UseSqlite(SqliteConnectionStrings.ForFile(paths.DatabasePath)));

        services.AddSingleton<DataSeeder>();
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<ISettingsStore, JsonSettingsStore>();
        services.AddSingleton<IAppStatusService, AppStatusService>();
        services.AddSingleton<IClientService, ClientService>();
        services.AddSingleton<ITaskService, TaskService>();
        services.AddSingleton<ILookupService, LookupService>();
        services.AddSingleton<IBoardService, BoardService>();
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<WorkTrace.Core.Screenshots.IScreenshotService, Screenshots.ScreenshotService>();
        services.AddSingleton<WorkTrace.Core.Reporting.ITimesheetService, Reporting.TimesheetService>();
        services.AddSingleton<WorkTrace.Core.Guides.IGuideService, GuideService>();
        services.AddSingleton<ICheckInService, CheckInService>();
        services.AddSingleton<ITrackingStore, TrackingStore>();

        // Requires an ICheckInPresenter registration from the host (the WPF app or tests).
        services.AddSingleton(sp => new TrackingEngine(
            sp.GetRequiredService<ITrackingStore>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ICheckInPresenter>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TrackingEngine>>(),
            sessionProbe: sp.GetService<ISessionStateProbe>()));

        return services;
    }
}
