namespace WorkTrace.Core.Entities;

/// <summary>
/// A lightweight reminder-only to-do. Deliberately separate from <see cref="WorkTask"/>: never tracked, never on the
/// Board, never offered where time is registered.
/// </summary>
public class MinorTask : IAuditedEntity, IChangeTracked
{
    public const int TitleMaxLength = 300;
    public const int DescriptionMaxLength = 2000;

    public int Id { get; set; }

    public required string Title { get; set; }

    public string? Description { get; set; }

    /// <summary>Optional stakeholder (cleared if the stakeholder is deleted).</summary>
    public int? ClientId { get; set; }

    public Client? Client { get; set; }

    /// <summary>When to remind (UTC); null = no reminder.</summary>
    public DateTime? ReminderAtUtc { get; set; }

    /// <summary>"Remind me later": not before this time. The reminder time itself is never changed by snoozing.</summary>
    public DateTime? SnoozedUntilUtc { get; set; }

    public bool IsCompleted { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}
