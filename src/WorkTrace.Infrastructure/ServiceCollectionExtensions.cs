using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WorkTrace.Core.Services;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Settings;
using WorkTrace.Infrastructure.Storage;

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

        return services;
    }
}
