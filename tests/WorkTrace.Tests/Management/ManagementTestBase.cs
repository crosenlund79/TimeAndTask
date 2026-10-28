using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Tests.Management;

/// <summary>Fresh, initialized database per test class instance (xUnit creates one per test).</summary>
public abstract class ManagementTestBase : IAsyncLifetime
{
    private readonly TestDataDirectory _data = new();

    protected ServiceProvider Services { get; private set; } = null!;

    protected IClientService Clients => Services.GetRequiredService<IClientService>();

    protected ITaskService Tasks => Services.GetRequiredService<ITaskService>();

    protected ILookupService Lookups => Services.GetRequiredService<ILookupService>();

    protected Microsoft.Extensions.Time.Testing.FakeTimeProvider Time => _data.Time;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Services = _data.BuildServices();
        await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        _data.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task<WorkTraceDbContext> DbAsync() =>
        await Services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);

    protected async Task<int> InternalClientIdAsync() =>
        (await Clients.GetClientsAsync(cancellationToken: Ct)).Single(c => c.Name == Client.DefaultInternalName).Id;

    protected async Task<int> LookupIdAsync(LookupKind kind, string name) =>
        (await Lookups.GetAsync(kind, cancellationToken: Ct)).Single(l => l.Name == name).Id;

    protected async Task<int> AddObservationAsync(int taskId)
    {
        await using var db = await DbAsync();
        var now = Time.GetUtcNow().UtcDateTime;
        var observation = Observation.Create(now, now, ObservationOrigin.Manual, taskId);
        db.Observations.Add(observation);
        await db.SaveChangesAsync(Ct);
        return observation.Id;
    }
}
