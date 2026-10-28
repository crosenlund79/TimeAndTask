using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>
/// Brings the database to a usable state at startup: creates directories, backs up an existing
/// database before applying migrations, migrates, enables WAL and seeds default data.
/// </summary>
public sealed class DatabaseInitializer(
    AppPaths paths,
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    DataSeeder seeder,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    /// <returns>True when the database file was newly created (first run).</returns>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureDirectories();
        var existed = File.Exists(paths.DatabasePath);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0)
        {
            if (existed && (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).Any())
            {
                BackupBeforeMigration(db);
            }

            logger.LogInformation("Applying {Count} database migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            await db.Database.MigrateAsync(cancellationToken);
        }

        // WAL is persistent in the file; setting it on every start is cheap and self-healing.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);

        await seeder.SeedAsync(db, cancellationToken);
        logger.LogInformation("Database ready ({State})", existed ? "existing" : "created");
        return !existed;
    }

    private void BackupBeforeMigration(WorkTraceDbContext db)
    {
        var stamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var target = Path.Combine(paths.BackupsDirectory, $"worktrace-premigration-{stamp}.db");

        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            connection.BackupDatabase(destination);
            logger.LogInformation("Created pre-migration backup {BackupFile}", Path.GetFileName(target));
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }
    }
}
