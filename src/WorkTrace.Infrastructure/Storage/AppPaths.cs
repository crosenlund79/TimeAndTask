namespace WorkTrace.Infrastructure.Storage;

/// <summary>
/// Resolves all per-user storage locations. Defaults to <c>%LocalAppData%\WorkTrace</c>; the
/// <see cref="DataDirectoryEnvironmentVariable"/> environment variable overrides it (dev/test).
/// The executable directory is never used for data.
/// </summary>
public sealed class AppPaths
{
    public const string DataDirectoryEnvironmentVariable = "WORKTRACE_DATA_DIR";
    public const string ApplicationFolderName = "WorkTrace";
    public const string DatabaseFileName = "worktrace.db";
    public const string SettingsFileName = "settings.json";

    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = Path.GetFullPath(dataDirectory);
    }

    public string DataDirectory { get; }

    public string DatabasePath => Path.Combine(DataDirectory, DatabaseFileName);

    public string SettingsPath => Path.Combine(DataDirectory, SettingsFileName);

    public string LogsDirectory => Path.Combine(DataDirectory, "logs");

    public string BackupsDirectory => Path.Combine(DataDirectory, "backups");

    public string ScreenshotsDirectory => Path.Combine(DataDirectory, "Screenshots");

    /// <summary>
    /// Resolves the data directory from (in order) an explicit override, the environment variable,
    /// and the per-user local application data folder.
    /// </summary>
    public static AppPaths Resolve(string? explicitDataDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitDataDirectory))
        {
            return new AppPaths(explicitDataDirectory);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return new AppPaths(fromEnvironment);
        }

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The per-user local application data folder could not be determined.");
        }

        return new AppPaths(Path.Combine(localAppData, ApplicationFolderName));
    }

    /// <summary>Creates the data directories that must exist before the application starts.</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
    }
}
