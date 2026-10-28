using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Tests.Infrastructure;

public class AppPathsTests
{
    [Fact]
    public void ExplicitDirectory_TakesPrecedence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wt-explicit");
        var paths = AppPaths.Resolve(dir);

        Assert.Equal(Path.GetFullPath(dir), paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "worktrace.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(paths.DataDirectory, "settings.json"), paths.SettingsPath);
        Assert.StartsWith(paths.DataDirectory, paths.LogsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(paths.DataDirectory, paths.ScreenshotsDirectory, StringComparison.Ordinal);
        Assert.StartsWith(paths.DataDirectory, paths.BackupsDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_IsUnderLocalApplicationData()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariable)))
        {
            Assert.Skip("WORKTRACE_DATA_DIR is set in this environment.");
        }

        var paths = AppPaths.Resolve();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "WorkTrace")), paths.DataDirectory);
        Assert.NotEqual(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar), paths.DataDirectory);
    }

    [Fact]
    public void BlankDirectory_IsRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(" "));
    }
}
