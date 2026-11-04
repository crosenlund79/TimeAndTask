namespace WorkTrace.Core.Entities;

/// <summary>
/// A reusable work item. Named WorkTask to avoid clashing with <see cref="System.Threading.Tasks.Task"/>.
/// </summary>
public class WorkTask : IAuditedEntity, IChangeTracked
{
    public const int TitleMaxLength = 500;
    public const int ExternalIdMaxLength = 100;
    public const int NotesMaxLength = 8000;
    public const int WaitingReasonMaxLength = 1000;

    public int Id { get; set; }

    public int ClientId { get; set; }

    public Client? Client { get; set; }

    public int? TaskSystemId { get; set; }

    public TaskSystem? TaskSystem { get; set; }

    /// <summary>
    /// Identifier in the external task system. Free-form string (prefixes, letters, leading zeros)
    /// and deliberately not unique.
    /// </summary>
    public string? ExternalId { get; set; }

    public required string Title { get; set; }

    public string? Notes { get; set; }

    /// <summary>Default channel suggested when observing this task; observations carry their own channel.</summary>
    public int? DefaultChannelId { get; set; }

    public CommunicationChannel? DefaultChannel { get; set; }

    public bool IsArchived { get; set; }

    /// <summary>Optional deadline (local date). Warnings start <c>Tasks.DeadlineWarningDays</c> before it.</summary>
    public DateOnly? Deadline { get; set; }

    /// <summary>Recurring work (meetings, routine duties): listed in its own group.</summary>
    public bool IsRecurring { get; set; }

    /// <summary>Waiting for information, a response, approval or action from someone else.</summary>
    public bool IsWaiting { get; set; }

    /// <summary>Optional "Waiting for / Reason" text while <see cref="IsWaiting"/>.</summary>
    public string? WaitingReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }

    /// <summary>Last time the task was used in an observation; drives "recent tasks".</summary>
    public DateTime? LastUsedAtUtc { get; set; }
}
