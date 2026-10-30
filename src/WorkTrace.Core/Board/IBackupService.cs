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

    /// <summary>The database snapshot, settings.json and guide files only (cards travel in monthly backups).</summary>
    Database = 4,
}

/// <param name="IncludeScreenshots">Complete backups: also the automatic screenshots folder.</param>
/// <param name="RecordAs">Change tracking per destination: "month:" (manual), "folder-month:", "email-month:".</param>
public sealed record BackupRequest(
    BackupKind Kind, string? Month = null, IReadOnlyList<int>? CardIds = null, bool IncludeScreenshots = true, string RecordAs = BackupRequest.ManualScope)
{
    public const string ManualScope = "month:";
}

public sealed record MonthCard(int CardId, long AttachmentBytes);

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
    /// <summary>Months that have cards, newest first, with change state since their last backup to <paramref name="recordAs"/>.</summary>
    Task<IReadOnlyList<BackupMonth>> GetMonthsAsync(string recordAs = BackupRequest.ManualScope, CancellationToken cancellationToken = default);

    /// <summary>Cards created in a month with the size of their attachments (to split oversized email backups).</summary>
    Task<IReadOnlyList<MonthCard>> GetMonthCardsAsync(string month, CancellationToken cancellationToken = default);

    /// <summary>Marks a month as backed up to a destination with the given content hash (after a split backup was fully sent).</summary>
    Task RecordMonthAsync(string month, string contentHash, string recordAs, string fileName, CancellationToken cancellationToken = default);

    /// <summary>Changes whenever relevant data changes (time registrations, tasks, stakeholders, board, guides, screenshots).</summary>
    Task<string> GetDataFingerprintAsync(CancellationToken cancellationToken = default);

    string DefaultFileName(BackupRequest request);

    /// <summary>Writes the archive to <paramref name="path"/> (atomically) and records it.</summary>
    Task<BackupResult> CreateAsync(BackupRequest request, string path, CancellationToken cancellationToken = default);
}
