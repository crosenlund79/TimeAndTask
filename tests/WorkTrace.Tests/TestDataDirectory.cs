using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using WorkTrace.Infrastructure;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Tests;

/// <summary>
/// An isolated, real on-disk data directory with a fully wired service provider.
/// Disposing removes the directory.
/// </summary>
internal sealed class TestDataDirectory : IDisposable
{
    public TestDataDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "worktrace-tests", Guid.NewGuid().ToString("N"));
        Paths = new AppPaths(Root);
    }

    public string Root { get; }

    public AppPaths Paths { get; }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    /// <summary>Builds a new provider, simulating an application start against the same data directory.</summary>
    public ServiceProvider BuildServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Debug));
        services.AddSingleton<TimeProvider>(Time);
        services.AddWorkTraceInfrastructure(Paths);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        // Pooled connections keep the file open (and locked on Windows).
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
