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

    /// <summary>Recurring reminder (a personal nudge): <see cref="ReminderAtUtc"/> is then the next occurrence.</summary>
    public MinorTaskRepeat RepeatKind { get; set; }

    public int RepeatIntervalMinutes { get; set; } = 60;

    /// <summary>Local time of day (minutes after midnight) for <see cref="MinorTaskRepeat.Daily"/>.</summary>
    public int RepeatDailyMinute { get; set; } = 10 * 60;

    /// <summary>Active weekdays as bits (Monday = 1 … Sunday = 64); Monday-Friday by default.</summary>
    public int RepeatDays { get; set; } = MinorTaskRecurrenceDays.Weekdays;

    /// <summary>Active hours for <see cref="MinorTaskRepeat.Interval"/>, local minutes after midnight.</summary>
    public int ActiveFromMinute { get; set; } = 8 * 60;

    public int ActiveUntilMinute { get; set; } = 16 * 60;

    /// <summary>"Disable reminder": no more notifications; the schedule is kept.</summary>
    public bool ReminderDisabled { get; set; }

    public DateTime? CompletedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }
}

public enum MinorTaskRepeat
{
    /// <summary>One-time reminder (Done completes the minor task).</summary>
    None = 0,

    /// <summary>Every N minutes within the active hours of the active days.</summary>
    Interval = 1,

    /// <summary>Once a day at a set time on the active days.</summary>
    Daily = 2,
}

public static class MinorTaskRecurrenceDays
{
    public const int Weekdays = 0b0011111;
    public const int All = 0b1111111;

    public static int Bit(DayOfWeek day) => 1 << (((int)day + 6) % 7);
}
