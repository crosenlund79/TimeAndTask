namespace WorkTrace.Core.Board;

public enum BackupKind
{
    /// <summary>Everything: all cards and screenshots, plus a snapshot of the whole database (time tracking) and settings.json.</summary>
    Complete = 0,

    /// <summary>All cards and screenshots.</summary>
    AllCards = 1,

    /// <summary>Cards created in one month (UTC) and their screenshots.</summary>
    Month = 2,

    /// <summary>Selected cards and their screenshots.</summary>
    Cards = 3,
}

public sealed record BackupRequest(BackupKind Kind, string? Month = null, IReadOnlyList<int>? CardIds = null);

/// <summary>A creation month (UTC, "2026-10") and whether it changed since its last backup.</summary>
public sealed record BackupMonth(string Month, int CardCount, int AttachmentCount, string ContentHash, DateTime? LastBackupAtUtc, bool ChangedSinceBackup);

public sealed record BackupResult(string Path, int CardCount, int AttachmentCount, long SizeBytes, bool IncludesDatabase);

/// <summary>
/// Backup and export as ZIP archives ("worktrace-board-backup" format, readable by the old browser board too).
/// Restoring cards is <see cref="IBoardService.PreviewImportAsync"/> / <see cref="IBoardService.ImportAsync"/>.
/// Nothing is sent anywhere: sending backups (e.g. by email) waits for an approved delivery method.
/// </summary>
public interface IBackupService
{
    /// <summary>Months that have cards, newest first, with change state since their last backup.</summary>
    Task<IReadOnlyList<BackupMonth>> GetMonthsAsync(CancellationToken cancellationToken = default);

    string DefaultFileName(BackupRequest request);

    /// <summary>Writes the archive to <paramref name="path"/> (atomically) and records it.</summary>
    Task<BackupResult> CreateAsync(BackupRequest request, string path, CancellationToken cancellationToken = default);
}
