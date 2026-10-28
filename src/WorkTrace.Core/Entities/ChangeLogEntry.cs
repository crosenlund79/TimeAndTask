namespace WorkTrace.Core.Entities;

public enum ChangeKind
{
    Insert = 0,
    Update = 1,
    Delete = 2,
}

/// <summary>
/// Append-only audit record of a persisted modification. Entries sharing a <see cref="BatchId"/>
/// form one user action; undo restores <see cref="BeforeJson"/> and is itself logged as a new batch.
/// </summary>
public class ChangeLogEntry
{
    public const int EntityTypeMaxLength = 100;

    public long Id { get; set; }

    public Guid BatchId { get; set; }

    public required string EntityType { get; set; }

    public int EntityId { get; set; }

    public ChangeKind Kind { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public DateTime ChangedAtUtc { get; set; }

    /// <summary>True when this batch reverses an earlier batch.</summary>
    public bool IsUndo { get; set; }

    public Guid? UndoesBatchId { get; set; }
}
