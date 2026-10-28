namespace WorkTrace.Core.Entities;

public enum AllocationKind
{
    /// <summary>The period was spent on <see cref="ManualAllocation.TaskId"/>.</summary>
    Task = 0,

    /// <summary>The period was explicitly not work (break, private errand).</summary>
    NonWork = 1,
}

/// <summary>
/// A user-asserted allocation of a time range. Allocations are layered over the estimated
/// timeline (most recently created wins on overlap), so recalculation can never discard them and
/// the flattened result cannot double-count time.
/// </summary>
public class ManualAllocation : IAuditedEntity
{
    public const int NoteMaxLength = 4000;

    public int Id { get; set; }

    public DateTime StartUtc { get; set; }

    public DateTime EndUtc { get; set; }

    public AllocationKind Kind { get; set; }

    public int? TaskId { get; set; }

    public WorkTask? Task { get; set; }

    public int? ChannelId { get; set; }

    public CommunicationChannel? Channel { get; set; }

    public string? Note { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}
