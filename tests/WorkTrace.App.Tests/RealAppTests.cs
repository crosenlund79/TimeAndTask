using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.App.Tests;

/// <summary>
/// End-to-end: launches the real WorkTrace.exe (own process, real clock, real timer, real tray app) with a
/// private data directory and a 1-minute interval, and waits for scheduled check-ins to appear on screen.
/// Unlike the in-process tests nothing is faked, so this is the test that proves scheduling works.
/// WorkTrace's own log is printed on failure.
/// </summary>
public partial class RealAppTests(ITestOutputHelper output)
{
    private const string PopupTitle = "WorkTrace check-in";
    private const string MainWindowTitle = "WorkTrace";
    private static readonly TimeSpan PopupWait = TimeSpan.FromSeconds(150); // next minute slot + start-up
    private static readonly TimeSpan AutoDismiss = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ScheduledCheckIns_AppearAndAutoClose_HiddenInTray_AndAfterRestart()
    {
        if (Mutex.TryOpenExisting(@"Local\WorkTrace.SingleInstance.6E1B9F4A", out var existing))
        {
            existing.Dispose();
            Assert.Skip("Another WorkTrace instance is running in this session.");
        }

        var exe = LocateApp();
        var root = Path.Combine(Path.GetTempPath(), "worktrace-realapp-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        Directory.CreateDirectory(root);
        try
        {
            // Run 1: first start (the main window opens on a first run), unobtrusive popups.
            WriteSettings(paths, PopupMode.Unobtrusive);
            using (var app = Start(exe, paths))
            {
                await ExpectScheduledPopupAsync(app, paths, expectedMissed: 1);
                Stop(app);
            }

            // Run 2: restart (scheduler re-initialised, crash recovery of run 1's segment), hidden in the
            // tray (StartMinimized, not a first run), focused popups.
            WriteSettings(paths, PopupMode.Focused);
            using (var app = Start(exe, paths))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.False(
                    FindWindows(app.Id, MainWindowTitle).Any(IsWindowVisible),
                    "The main window should stay hidden in the tray on a normal start");
                await ExpectScheduledPopupAsync(app, paths, expectedMissed: 2);
                Stop(app);
            }

            var segments = Query(paths, "SELECT COUNT(*) FROM TrackingSegments");
            Assert.True(segments >= 2, $"Expected a tracking segment per run, found {segments}");
        }
        catch (Exception) when (Dump(paths))
        {
            throw;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task ExpectScheduledPopupAsync(Process app, AppPaths paths, int expectedMissed)
    {
        var started = Stopwatch.StartNew();
        var popup = await WaitForAsync(() => FindWindows(app.Id, PopupTitle).FirstOrDefault(IsWindowVisible), PopupWait, app);
        Assert.True(popup != IntPtr.Zero, $"No scheduled check-in appeared within {PopupWait.TotalSeconds:0} s (1-minute interval)");
        output.WriteLine($"Scheduled popup appeared after {started.Elapsed.TotalSeconds:0} s; foreground={GetForegroundWindow() == popup}");

        // Not answered: it must close by itself and be stored as missed.
        var closed = await WaitForAsync(() => FindWindows(app.Id, PopupTitle).Any(IsWindowVisible) ? IntPtr.Zero : 1, AutoDismiss + TimeSpan.FromSeconds(20), app);
        Assert.True(closed != IntPtr.Zero, "The unanswered popup did not close after its countdown");

        var missed = await WaitForAsync(
            () => Query(paths, $"SELECT COUNT(*) FROM CheckInPrompts WHERE Outcome = {(int)CheckInOutcome.Missed}") >= expectedMissed ? 1 : IntPtr.Zero,
            TimeSpan.FromSeconds(10),
            app);
        Assert.True(missed != IntPtr.Zero, $"Expected {expectedMissed} missed check-in(s) in the database");
    }

    private static async Task<IntPtr> WaitForAsync(Func<IntPtr> probe, TimeSpan timeout, Process app)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            Assert.False(app.HasExited, $"WorkTrace exited unexpectedly (code {(app.HasExited ? app.ExitCode : 0)})");
            if (probe() is var found && found != IntPtr.Zero)
            {
                return found;
            }

            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        return IntPtr.Zero;
    }

    private static Process Start(string exe, AppPaths paths)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        info.Environment[AppPaths.DataDirectoryEnvironmentVariable] = paths.DataDirectory;
        return Process.Start(info)!;
    }

    /// <summary>Ends the app abruptly; the next start must recover the open segment.</summary>
    private static void Stop(Process app)
    {
        if (!app.HasExited)
        {
            app.Kill(entireProcessTree: true);
            app.WaitForExit(10_000);
        }
    }

    private static void WriteSettings(AppPaths paths, PopupMode mode)
    {
        var settings = File.Exists(paths.SettingsPath)
            ? JsonSerializer.Deserialize<WorkTraceSettings>(File.ReadAllText(paths.SettingsPath), JsonOptions)!
            : new WorkTraceSettings();
        settings.General.StartMinimized = true;
        settings.Tracking.Enabled = true;
        settings.Tracking.PausedUntilUtc = null;
        settings.Tracking.PeriodicPromptsEnabled = true;
        settings.Tracking.PromptOnStart = false; // only genuine interval slots
        settings.Tracking.CheckInIntervalMinutes = 1;
        settings.Tracking.AutoDismissSeconds = (int)AutoDismiss.TotalSeconds;
        settings.Tracking.PopupMode = mode;
        File.WriteAllText(paths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static long Query(AppPaths paths, string sql)
    {
        if (!File.Exists(paths.DatabasePath))
        {
            return 0;
        }

        using var connection = new SqliteConnection($"Data Source={paths.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Prints WorkTrace's log (exception filter: runs before the failure propagates).</summary>
    private bool Dump(AppPaths paths)
    {
        try
        {
            if (Directory.Exists(paths.LogsDirectory))
            {
                foreach (var file in Directory.GetFiles(paths.LogsDirectory))
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    output.WriteLine($"----- {Path.GetFileName(file)}");
                    output.WriteLine(reader.ReadToEnd());
                }
            }

            if (File.Exists(paths.DatabasePath))
            {
                output.WriteLine($"Prompts: {Query(paths, "SELECT COUNT(*) FROM CheckInPrompts")}, segments: {Query(paths, "SELECT COUNT(*) FROM TrackingSegments")}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"(could not dump diagnostics: {ex.Message})");
        }

        return false;
    }

    /// <summary>The app's own build output (same configuration as this test assembly).</summary>
    private static string LocateApp()
    {
        var baseDir = AppContext.BaseDirectory;
        var configuration = baseDir.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        for (var dir = new DirectoryInfo(baseDir); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TimeAndTask.slnx")))
            {
                var exe = Path.Combine(dir.FullName, "src", "WorkTrace.App", "bin", configuration, "net10.0-windows", "WorkTrace.exe");
                Assert.True(File.Exists(exe), $"WorkTrace.exe not found at {exe}; build the solution first");
                return exe;
            }
        }

        var local = Path.Combine(baseDir, "WorkTrace.exe");
        Assert.True(File.Exists(local), "WorkTrace.exe not found");
        return local;
    }

    private static List<IntPtr> FindWindows(int processId, string title)
    {
        var found = new List<IntPtr>();
        var hwnd = IntPtr.Zero;
        while ((hwnd = FindWindowExW(IntPtr.Zero, hwnd, null, title)) != IntPtr.Zero)
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == processId)
            {
                found.Add(hwnd);
            }
        }

        return found;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();
}
