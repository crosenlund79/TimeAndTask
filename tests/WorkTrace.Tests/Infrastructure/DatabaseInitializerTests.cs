using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Services;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Tests.Infrastructure;

public class DatabaseInitializerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FreshStart_CreatesDatabase_AppliesAllMigrations_AndSeeds()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();

        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);

        Assert.True(File.Exists(data.Paths.DatabasePath));
        Assert.True(Directory.Exists(data.Paths.LogsDirectory));
        Assert.True(Directory.Exists(data.Paths.BackupsDirectory));
        Assert.Empty(Directory.GetFiles(data.Paths.BackupsDirectory));

        await using var db = await services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync(Ct));

        var client = Assert.Single(await db.Clients.ToListAsync(Ct));
        Assert.Equal(Client.DefaultInternalName, client.Name);
        Assert.True(client.IsInternal);
        Assert.True(client.IsActive);
        Assert.Equal(DateTimeKind.Utc, client.CreatedAtUtc.Kind);

        Assert.Equal(TaskSystem.BuiltInNames, await db.TaskSystems.OrderBy(t => t.SortOrder).Select(t => t.Name).ToListAsync(Ct));
        Assert.Equal(CommunicationChannel.BuiltInNames, await db.CommunicationChannels.OrderBy(c => c.SortOrder).Select(c => c.Name).ToListAsync(Ct));
    }

    [Fact]
    public async Task Initialize_IsIdempotent_AndRespectsRenamedSeedData()
    {
        using var data = new TestDataDirectory();

        await using (var services = data.BuildServices())
        {
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
            await using var db = await services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
            var client = await db.Clients.SingleAsync(Ct);
            client.Name = "Intern";
            await db.SaveChangesAsync(Ct);
        }

        // Simulated restart against the same data directory.
        await using (var services = data.BuildServices())
        {
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
            await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);

            await using var db = await services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
            Assert.Equal("Intern", Assert.Single(await db.Clients.ToListAsync(Ct)).Name);
            Assert.Equal(TaskSystem.BuiltInNames.Count, await db.TaskSystems.CountAsync(Ct));
            Assert.Equal(CommunicationChannel.BuiltInNames.Count, await db.CommunicationChannels.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Database_UsesWalJournal_AndEnforcesForeignKeys()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);

        await using var db = await services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(Ct);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync(Ct));
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, (long?)await cmd.ExecuteScalarAsync(Ct));
        }
    }

    [Fact]
    public async Task StatusService_ReportsStorageState()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);

        var status = await services.GetRequiredService<IAppStatusService>().GetStatusAsync(Ct);

        Assert.Equal(data.Paths.DatabasePath, status.DatabasePath);
        Assert.NotEmpty(status.AppliedMigrations);
        Assert.Equal(1, status.ClientCount);
        Assert.Equal(0, status.TaskCount);
        Assert.Equal(TaskSystem.BuiltInNames.Count, status.TaskSystemCount);
    }

    [Fact]
    public async Task Initialize_DoesNotUseExecutableDirectory()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);

        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "worktrace.db")));
        SqliteConnection.ClearAllPools();
    }
}
