namespace WorkTrace.Core.Management;

/// <param name="ReminderAtUtc">Null = no reminder. Ignored for a recurring one (the next occurrence is computed).</param>
/// <param name="Recurrence">Null or <see cref="Entities.MinorTaskRepeat.None"/> = one-time.</param>
public sealed record MinorTaskInput(string? Title, string? Description = null, int? ClientId = null, DateTime? ReminderAtUtc = null,
    MinorTaskRecurrence? Recurrence = null);

public sealed record MinorTaskSummary(
    int Id,
    string Title,
    string? Description,
    int? ClientId,
    string? ClientName,
    string? ClientColor,
    DateTime? ReminderAtUtc,
    DateTime? SnoozedUntilUtc,
    bool IsCompleted,
    DateTime? CompletedAtUtc,
    DateTime CreatedAtUtc,
    MinorTaskRecurrence? Recurrence = null,
    bool ReminderDisabled = false)
{
    public bool IsRecurring => Recurrence is not null;
}

/// <summary>Minor tasks: reminder-only to-dos, independent of tasks, time registration and the Board.</summary>
public interface IMinorTaskService
{
    /// <summary>Open ones first (by reminder, then creation); completed ones newest first.</summary>
    Task<IReadOnlyList<MinorTaskSummary>> GetAsync(bool includeCompleted, CancellationToken cancellationToken = default);

    Task<MinorTaskSummary> CreateAsync(MinorTaskInput input, CancellationToken cancellationToken = default);

    /// <summary>Changing the reminder time also clears any snooze.</summary>
    Task<MinorTaskSummary> UpdateAsync(int id, MinorTaskInput input, CancellationToken cancellationToken = default);

    Task<MinorTaskSummary> SetCompletedAsync(int id, bool completed, CancellationToken cancellationToken = default);

    /// <summary>"Remind me later": snoozes until <paramref name="untilUtc"/> without changing the reminder time.</summary>
    Task SnoozeAsync(int id, DateTime untilUtc, CancellationToken cancellationToken = default);

    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>
    /// The reminder's Done: a one-time minor task is completed; a recurring one only acknowledges this occurrence and moves to
    /// the next one (the minor task stays open, no new record).
    /// </summary>
    Task AcknowledgeAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>"Disable reminder" (or enable it again): stops future notifications, keeps the minor task and its schedule.</summary>
    Task SetReminderDisabledAsync(int id, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Open minor tasks whose reminder time has come and that are not snoozed (oldest reminder first). A recurring one that is
    /// due outside its active period (e.g. missed while WorkTrace was closed) is moved to its next occurrence instead.
    /// </summary>
    Task<IReadOnlyList<MinorTaskSummary>> GetDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
