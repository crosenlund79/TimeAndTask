namespace WorkTrace.Core.Management;

/// <param name="ReminderAtUtc">Null = no reminder.</param>
public sealed record MinorTaskInput(string? Title, string? Description = null, int? ClientId = null, DateTime? ReminderAtUtc = null);

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
    DateTime CreatedAtUtc);

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

    /// <summary>Open minor tasks whose reminder time has come and that are not snoozed (oldest reminder first).</summary>
    Task<IReadOnlyList<MinorTaskSummary>> GetDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
