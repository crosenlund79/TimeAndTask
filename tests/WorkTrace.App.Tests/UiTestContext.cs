using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using WorkTrace.App.Shell;
using WorkTrace.Infrastructure;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Tests;

/// <summary>The real app object graph over a throw-away data directory. Create and use on the UI thread.</summary>
internal sealed class UiTestContext : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "worktrace-ui-tests", Guid.NewGuid().ToString("N"));

    private UiTestContext()
    {
    }

    public ServiceProvider Services { get; private set; } = null!;

    public FakeDialogService Dialogs { get; } = new();

    public FakeFileDialogService Files { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero));

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <param name="realTime">Use the system clock (real timers) instead of <see cref="Time"/>.</param>
    public static async Task<UiTestContext> CreateAsync(bool realTime = false)
    {
        WorkTrace.App.Views.CheckInWindow.CaptureCreationTraces = true;
        var context = new UiTestContext();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(realTime ? TimeProvider.System : context.Time);
        services.AddWorkTraceInfrastructure(new AppPaths(context._root));
        services.AddWorkTraceApp(new FakeLifetime());
        services.AddSingleton<IDialogService>(context.Dialogs);
        services.AddSingleton<IFileDialogService>(context.Files);
        context.Services = services.BuildServiceProvider();

        await context.Get<DatabaseInitializer>().InitializeAsync();
        await context.Get<ISettingsStore>().LoadAsync();
        return context;
    }

    /// <summary>Check-in popups that exist right now (to find the one a test opens).</summary>
    public static IReadOnlyList<WorkTrace.App.Views.CheckInWindow> Popups() =>
        [.. System.Windows.Application.Current.Windows.OfType<WorkTrace.App.Views.CheckInWindow>()];

    /// <summary>Describes popups for assertion messages (to diagnose unexpected extra windows).</summary>
    public static string Describe(IEnumerable<WorkTrace.App.Views.CheckInWindow> popups) =>
        string.Join("\n", popups.Select(p =>
            $"[{(p.DataContext as WorkTrace.App.ViewModels.CheckInViewModel)?.Subheading ?? "?"}, visible={p.IsVisible}, loaded={p.IsLoaded}]\n" +
            string.Join("\n", (p.CreationTrace ?? "(no trace)").Split('\n')
                .Where(l => l.Contains("WorkTrace", StringComparison.Ordinal))
                .Take(8))));

    public async ValueTask DisposeAsync()
    {
        // As the app does on exit: stop tracking while its services still exist (a started engine would
        // otherwise be stopped during container disposal, when the database factory is already unusable).
        await Get<WorkTrace.Infrastructure.Tracking.TrackingEngine>().StopAsync();

        // Never leave windows behind for the next test (they share one Application).
        foreach (var window in System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().ToList())
        {
            window.Close();
        }

        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeLifetime : IAppLifetime
    {
        public bool IsExiting => true;

        public void RequestExit()
        {
        }
    }
}

internal sealed class FakeDialogService : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<string> Asked { get; } = [];

    public bool Confirm(string title, string message, string confirmText, bool destructive = false)
    {
        Asked.Add(title);
        return Answer;
    }
}

internal sealed class FakeFileDialogService : IFileDialogService
{
    public List<string> FilesToOpen { get; } = [];

    public List<string> Opened { get; } = [];

    public string? SavePath { get; set; }

    public IReadOnlyList<string> OpenFiles(string title, string filter, bool multiselect = true) => [.. FilesToOpen];

    public string? SaveFile(string title, string filter, string defaultFileName) => SavePath;

    public string? ChooseFolder(string title, string? initial) => SavePath;

    public void OpenUrl(string url) => Opened.Add(url);

    public void OpenFile(string path) => Opened.Add(path);

    public void CopyText(string text) => Opened.Add("copied:" + text);
}
