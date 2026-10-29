namespace WorkTrace.Core.Entities;

/// <summary>
/// A backup that was written successfully. Per-month content hashes tell which months changed since their
/// last backup, so a scheduled (e.g. daily email) backup can send only what changed.
/// </summary>
public class BackupRecord
{
    public const int ScopeMaxLength = 40;
    public const int FileNameMaxLength = 260;

    public int Id { get; set; }

    /// <summary>"month:2026-10" for each month a backup covered.</summary>
    public required string Scope { get; set; }

    public required string ContentHash { get; set; }

    public required string FileName { get; set; }

    public long SizeBytes { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
