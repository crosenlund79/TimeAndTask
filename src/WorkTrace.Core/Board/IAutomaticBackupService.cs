namespace WorkTrace.Core.Board;

/// <summary>Secrets (e.g. the email API key), stored outside settings.json and backups. Windows: Credential Manager.</summary>
public interface ISecretStore
{
    string? Read(string name);

    /// <summary>Stores the secret, or removes it when <paramref name="value"/> is null or empty.</summary>
    void Write(string name, string? value);
}

/// <summary>Sends one email with one attachment. Implementations never log or echo the key.</summary>
public interface IBackupMailer
{
    Task SendAsync(string sender, string recipient, string subject, string body, string attachmentName, byte[] attachment, CancellationToken cancellationToken = default);
}

public enum BackupDestination
{
    Folder = 0,
    Email = 1,
}

public sealed record BackupRunResult(BackupDestination Destination, bool Ran, bool Succeeded, string Message);

/// <summary>
/// Scheduled backups to a folder and by email. Each runs when enabled, due (daily or weekly) and data changed since its
/// last success; "Backup now" / "Send now" force a run.
/// </summary>
public interface IAutomaticBackupService
{
    public const string EmailKeySecret = "WorkTrace/ResendApiKey";

    Task<IReadOnlyList<BackupRunResult>> RunDueAsync(CancellationToken cancellationToken = default);

    Task<BackupRunResult> RunNowAsync(BackupDestination destination, CancellationToken cancellationToken = default);
}
