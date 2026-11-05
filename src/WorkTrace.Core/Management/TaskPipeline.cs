namespace WorkTrace.Core.Management;

/// <summary>
/// One task in the planning queue. <see cref="IsProvisional"/>: no estimate, planned with <see cref="TaskPipeline.ProvisionalMinutes"/>.
/// <see cref="StartDay"/>/<see cref="EndDay"/> are positions on the working-day axis (0 = start of the first day; 1.5 = midday of
/// the second).
/// </summary>
public sealed record QueueEntry(
    TaskSummary Task,
    int Position,
    int StartMinutes,
    int DurationMinutes,
    bool IsProvisional,
    double StartDay = 0,
    double EndDay = 0,
    DateOnly StartDate = default,
    DateOnly EndDate = default);

/// <summary>A working day (Monday-Friday) on the planning axis; public holidays have no capacity.</summary>
public sealed record PlanDay(DateOnly Date, Holiday? Holiday)
{
    public int CapacityMinutes => Holiday?.Kind == HolidayKind.Public ? 0 : TaskPipeline.WorkingDayMinutes;
}

public sealed record PipelinePlan(IReadOnlyList<QueueEntry> Entries, IReadOnlyList<PlanDay> Days);

/// <summary>
/// The active task pipeline for planning, in chronological order: tasks with a planned start at that date, the rest
/// filling the days in priority order (Board card priority and order, deadline, recent use). Effort comes from the estimate; tasks without
/// one get a provisional block that is never stored. Work is laid out on Monday-Friday days of 7.5 hours, from the
/// first working day on or after <c>from</c>; Danish public holidays and weekends get no work, and a task never
/// starts before its planned start.
/// </summary>
public static class TaskPipeline
{
    public const int ProvisionalMinutes = 240;
    public const int WorkingDayMinutes = 450;
    private const int MaxDays = 60;

    /// <param name="includeClosed">Closed tasks are left out unless asked for; archived tasks always are.</param>
    /// <param name="includeRecurring">Recurring tasks are not active work; they appear only under their own filter.</param>
    public static IReadOnlyList<QueueEntry> Build(IEnumerable<TaskSummary> tasks, DateOnly from, bool includeClosed = false, bool includeRecurring = false) =>
        Plan(tasks, from, includeClosed, includeRecurring).Entries;

    public static PipelinePlan Plan(IEnumerable<TaskSummary> tasks, DateOnly from, bool includeClosed = false, bool includeRecurring = false)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var active = tasks
            .Where(t => (includeClosed || !t.IsClosed) && !t.IsArchived && (includeRecurring || t.Group != TaskGroup.Recurring))
            .ToList();
        var planned = new Queue<TaskSummary>(active.Where(t => t.PlannedStart is not null).OrderBy(t => t.PlannedStart).ThenBy(t => t.Id));
        var unplanned = new Queue<TaskSummary>(active.Where(t => t.PlannedStart is null)
            .OrderByDescending(t => t.BoardPriority ?? Entities.BoardPriority.Normal)
            .ThenBy(t => t.Deadline is null)
            .ThenBy(t => t.Deadline)
            .ThenBy(t => t.BoardSortOrder ?? double.MaxValue)
            .ThenByDescending(t => t.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenBy(t => t.Id));

        var days = new List<PlanDay>();
        PlanDay Day(int index)
        {
            while (days.Count <= index)
            {
                var next = days.Count == 0 ? NextWeekday(from) : NextWeekday(days[^1].Date.AddDays(1));
                days.Add(new PlanDay(next, DanishHolidays.Get(next)));
            }

            return days[index];
        }

        int IndexOf(DateOnly date)
        {
            var i = 0;
            while (i < MaxDays * 4 && Day(i).Date < date)
            {
                i++;
            }

            return i;
        }

        var (day, minute, start) = (0, 0, 0);
        void SkipFull()
        {
            while (minute >= Day(day).CapacityMinutes)
            {
                (day, minute) = (day + 1, 0);
            }
        }

        // Chronological: unplanned work (by priority) fills the days from today; a planned task enters the queue once
        // the schedule reaches its planned start, and never starts before it.
        var result = new List<QueueEntry>();
        while (planned.Count > 0 || unplanned.Count > 0)
        {
            SkipFull();
            var task = planned.Count > 0 && (unplanned.Count == 0 || IndexOf(planned.Peek().PlannedStart!.Value) <= day)
                ? planned.Dequeue()
                : unplanned.Dequeue();
            if (task.PlannedStart is { } plannedStart && IndexOf(plannedStart) is var plannedDay && plannedDay > day)
            {
                (day, minute) = (plannedDay, 0);
            }

            var provisional = task.EstimatedRemainingMinutes is null;
            var duration = task.EstimatedRemainingMinutes ?? ProvisionalMinutes;
            SkipFull();
            var startDay = day + (minute / (double)WorkingDayMinutes);
            var startDate = Day(day).Date;
            for (var remaining = duration; remaining > 0;)
            {
                SkipFull();
                var take = Math.Min(remaining, Day(day).CapacityMinutes - minute);
                minute += take;
                remaining -= take;
            }

            var endDay = day + (minute / (double)WorkingDayMinutes);
            result.Add(new QueueEntry(task, result.Count + 1, start, duration, provisional, startDay, endDay, startDate, Day(day).Date));
            start += duration;
        }

        var shown = Math.Clamp((int)Math.Ceiling(result.Count == 0 ? 0 : result.Max(e => e.EndDay)), 5, MaxDays);
        Day(shown - 1);
        return new PipelinePlan(result, days.Take(shown).ToList());
    }

    private static DateOnly NextWeekday(DateOnly date) =>
        date.DayOfWeek switch { DayOfWeek.Saturday => date.AddDays(2), DayOfWeek.Sunday => date.AddDays(1), _ => date };

    /// <summary>"2 h 30 min", "45 min", "3 h".</summary>
    public static string FormatDuration(int minutes) => (minutes / 60, minutes % 60) switch
    {
        (0, var m) => $"{m} min",
        (var h, 0) => $"{h} h",
        (var h, var m) => $"{h} h {m} min",
    };
}
