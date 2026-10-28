using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Services;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Persistence;

internal sealed class AppStatusService(AppPaths paths, IDbContextFactory<WorkTraceDbContext> contextFactory) : IAppStatusService
{
    public async Task<AppStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return new AppStatus(
            paths.DataDirectory,
            paths.DatabasePath,
            paths.LogsDirectory,
            [.. await db.Database.GetAppliedMigrationsAsync(cancellationToken)],
            await db.Clients.CountAsync(cancellationToken),
            await db.Tasks.CountAsync(cancellationToken),
            await db.Observations.CountAsync(o => !o.IsDeleted, cancellationToken),
            await db.TaskSystems.CountAsync(cancellationToken),
            await db.CommunicationChannels.CountAsync(cancellationToken));
    }
}
