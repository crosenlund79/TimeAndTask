using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Tests.Infrastructure;

public class PersistenceTests
{
    private static readonly DateTime T = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(TestDataDirectory Data, ServiceProvider Services, IDbContextFactory<WorkTraceDbContext> Factory)> StartAsync()
    {
        var data = new TestDataDirectory();
        var services = data.BuildServices();
        await services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
        return (data, services, services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>());
    }

    [Fact]
    public async Task ClientTaskAndObservation_PersistAcrossRestart()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        int observationId;

        await using (services)
        {
            await using var db = await factory.CreateDbContextAsync(Ct);
            var client = new Client { Name = "Contoso", CreatedAtUtc = T, ModifiedAtUtc = T };
            var system = await db.TaskSystems.SingleAsync(s => s.Name == "D365FO", Ct);
            var task = new WorkTask
            {
                Client = client,
                TaskSystem = system,
                ExternalId = "00042-B",
                Title = "Invoice posting error",
                CreatedAtUtc = T,
                ModifiedAtUtc = T,
            };
            db.Tasks.Add(task);
            await db.SaveChangesAsync(Ct);

            var observation = Observation.Create(T, T.AddSeconds(3), ObservationOrigin.Scheduled, task.Id, note: "debugging");
            db.Observations.Add(observation);
            await db.SaveChangesAsync(Ct);
            observationId = observation.Id;
        }

        await using var restarted = data.BuildServices();
        await using var db2 = await restarted.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        var loaded = await db2.Observations.Include(o => o.Task!).ThenInclude(t => t.Client).SingleAsync(o => o.Id == observationId, Ct);

        Assert.Equal(T, loaded.ObservedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.ObservedAtUtc.Kind);
        Assert.Equal(ObservationOrigin.Scheduled, loaded.Origin);
        Assert.Equal("00042-B", loaded.Task!.ExternalId);
        Assert.Equal("Contoso", loaded.Task.Client!.Name);
        Assert.False(loaded.IsCorrected);
    }

    [Fact]
    public async Task ObservationCorrection_PreservesOriginalValuesInDatabase()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        int id;
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var o = Observation.Create(T, T, ObservationOrigin.Manual, taskId: null, note: "original");
            db.Observations.Add(o);
            await db.SaveChangesAsync(Ct);
            id = o.Id;
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var o = await db.Observations.SingleAsync(x => x.Id == id, Ct);
            o.ObservedAtUtc = T.AddMinutes(-15);
            o.Note = "corrected";
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var o = await db.Observations.SingleAsync(x => x.Id == id, Ct);
            Assert.True(o.IsCorrected);
            Assert.Equal(T, o.OriginalObservedAtUtc);
            Assert.Equal("original", o.OriginalNote);
            Assert.Equal("corrected", o.Note);
        }
    }

    [Fact]
    public async Task DeletingReferencedClient_IsRejected()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var client = await db.Clients.SingleAsync(Ct);
            db.Tasks.Add(new WorkTask { ClientId = client.Id, Title = "Team meeting", CreatedAtUtc = T, ModifiedAtUtc = T });
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            // Bypass EF change tracking to prove the constraint lives in the database itself.
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                db.Database.ExecuteSqlRawAsync("DELETE FROM Clients;", Ct));
            Assert.Equal(1, await db.Clients.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task TaskWithUnknownClient_IsRejected()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using var db = await factory.CreateDbContextAsync(Ct);
        db.Tasks.Add(new WorkTask { ClientId = 999, Title = "Orphan", CreatedAtUtc = T, ModifiedAtUtc = T });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ClientNames_AreUniqueCaseInsensitive()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using var db = await factory.CreateDbContextAsync(Ct);
        db.Clients.Add(new Client { Name = "INTERNAL", CreatedAtUtc = T, ModifiedAtUtc = T });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ExternalIds_AreNotRequiredToBeUnique()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using var db = await factory.CreateDbContextAsync(Ct);
        var clientId = (await db.Clients.SingleAsync(Ct)).Id;
        db.Tasks.Add(new WorkTask { ClientId = clientId, ExternalId = "0001", Title = "A", CreatedAtUtc = T, ModifiedAtUtc = T });
        db.Tasks.Add(new WorkTask { ClientId = clientId, ExternalId = "0001", Title = "B", CreatedAtUtc = T, ModifiedAtUtc = T });
        await db.SaveChangesAsync(Ct);

        Assert.Equal(2, await db.Tasks.CountAsync(t => t.ExternalId == "0001", Ct));
    }

    [Fact]
    public async Task UtcTimestamps_SortChronologically_InQueries()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            foreach (var minutes in new[] { 90, 5, 600, 30 })
            {
                db.Observations.Add(Observation.Create(T.AddMinutes(minutes), T, ObservationOrigin.Manual, null));
            }

            await db.SaveChangesAsync(Ct);
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var from = T.AddMinutes(10);
            var ordered = await db.Observations
                .Where(o => o.ObservedAtUtc >= from)
                .OrderBy(o => o.ObservedAtUtc)
                .Select(o => o.ObservedAtUtc)
                .ToListAsync(Ct);

            Assert.Equal([T.AddMinutes(30), T.AddMinutes(90), T.AddMinutes(600)], ordered);
        }
    }

    [Fact]
    public async Task UnspecifiedTimestamp_IsRejectedOnSave()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using var db = await factory.CreateDbContextAsync(Ct);
        db.TrackingSegments.Add(new TrackingSegment
        {
            StartUtc = DateTime.SpecifyKind(T, DateTimeKind.Unspecified),
            LastHeartbeatUtc = T,
        });
        await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ManualAllocation_CheckConstraints_AreEnforced()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using var db = await factory.CreateDbContextAsync(Ct);
        db.ManualAllocations.Add(new ManualAllocation
        {
            StartUtc = T,
            EndUtc = T.AddMinutes(30),
            Kind = AllocationKind.Task,
            TaskId = null,
            CreatedAtUtc = T,
            ModifiedAtUtc = T,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Transaction_RollsBackAllChanges()
    {
        var (data, services, factory) = await StartAsync();
        using var _ = data;
        await using var __ = services;

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            await using var tx = await db.Database.BeginTransactionAsync(Ct);
            db.Clients.Add(new Client { Name = "Fabrikam", CreatedAtUtc = T, ModifiedAtUtc = T });
            await db.SaveChangesAsync(Ct);
            await tx.RollbackAsync(Ct);
        }

        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            Assert.False(await db.Clients.AnyAsync(c => c.Name == "Fabrikam", Ct));
        }
    }
}
