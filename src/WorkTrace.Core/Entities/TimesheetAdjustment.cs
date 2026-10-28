namespace WorkTrace.Core.Entities;

/// <summary>
/// A manual override of the reported minutes for one task on one local day. It never modifies
/// observations or allocations. <see cref="BaselineMinutes"/> records the computed value at the time
/// of the adjustment so later divergence can be flagged instead of silently resolved.
/// </summary>
public class TimesheetAdjustment : IAuditedEntity
{
    public const int NoteMaxLength = 2000;

    public int Id { get; set; }

    public DateOnly LocalDate { get; set; }

    public int TaskId { get; set; }

    public WorkTask? Task { get; set; }

    public int AdjustedMinutes { get; set; }

    public int BaselineMinutes { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}
