namespace WorkTrace.Core.Management;

/// <summary>One task in the planning queue. <see cref="IsProvisional"/>: no estimate, planned with <see cref="TaskPipeline.ProvisionalMinutes"/>.</summary>
public sealed record QueueEntry(TaskSummary Task, int Position, int StartMinutes, int DurationMinutes, bool IsProvisional);

/// <summary>
/// The active task pipeline for planning: explicitly scheduled tasks first (by planned start), then the rest by
/// priority (Board card priority and order), deadline and recent use. Effort comes from the estimate; tasks without
/// one get a provisional block that is never stored.
/// </summary>
public static class TaskPipeline
{
    public const int ProvisionalMinutes = 240;

    /// <param name="includeClosed">Closed tasks are left out unless asked for; archived tasks always are.</param>
    public static IReadOnlyList<QueueEntry> Build(IEnumerable<TaskSummary> tasks, bool includeClosed = false)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var ordered = tasks
            .Where(t => (includeClosed || !t.IsClosed) && !t.IsArchived)
            .OrderBy(t => t.PlannedStart is null)
            .ThenBy(t => t.PlannedStart)
            .ThenByDescending(t => t.BoardPriority ?? Entities.BoardPriority.Normal)
            .ThenBy(t => t.Deadline is null)
            .ThenBy(t => t.Deadline)
            .ThenBy(t => t.BoardSortOrder ?? double.MaxValue)
            .ThenByDescending(t => t.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenBy(t => t.Id);

        var result = new List<QueueEntry>();
        var start = 0;
        foreach (var task in ordered)
        {
            var provisional = task.EstimatedRemainingMinutes is null;
            var duration = task.EstimatedRemainingMinutes ?? ProvisionalMinutes;
            result.Add(new QueueEntry(task, result.Count + 1, start, duration, provisional));
            start += duration;
        }

        return result;
    }

    /// <summary>"2 h 30 min", "45 min", "3 h".</summary>
    public static string FormatDuration(int minutes) => (minutes / 60, minutes % 60) switch
    {
        (0, var m) => $"{m} min",
        (var h, 0) => $"{h} h",
        (var h, var m) => $"{h} h {m} min",
    };
}
