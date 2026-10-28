namespace WorkTrace.Core.Services;

/// <summary>Snapshot of storage health shown in the main window.</summary>
public sealed record AppStatus(
    string DataDirectory,
    string DatabasePath,
    string LogsDirectory,
    IReadOnlyList<string> AppliedMigrations,
    int ClientCount,
    int TaskCount,
    int ObservationCount,
    int TaskSystemCount,
    int ChannelCount);

public interface IAppStatusService
{
    Task<AppStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
