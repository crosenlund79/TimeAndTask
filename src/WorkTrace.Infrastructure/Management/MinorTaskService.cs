using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Management;

internal sealed class MinorTaskService(IDbContextFactory<WorkTraceDbContext> contextFactory, TimeProvider timeProvider) : IMinorTaskService
{
    public async Task<IReadOnlyList<MinorTaskSummary>> GetAsync(bool includeCompleted, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var all = await Summaries(db.MinorTasks.Where(m => includeCompleted || !m.IsCompleted)).ToListAsync(cancellationToken);
        return
        [
            .. all.Where(m => !m.IsCompleted).OrderBy(m => m.ReminderAtUtc is null).ThenBy(m => m.ReminderAtUtc).ThenBy(m => m.CreatedAtUtc).ThenBy(m => m.Id),
            .. all.Where(m => m.IsCompleted).OrderByDescending(m => m.CompletedAtUtc).ThenByDescending(m => m.Id),
        ];
    }

    public async Task<MinorTaskSummary> CreateAsync(MinorTaskInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateAsync(db, input, cancellationToken);
        var now = UtcTime.Now(timeProvider);
        var task = new MinorTask { Title = Text.Clean(input.Title)!, CreatedAtUtc = now, ModifiedAtUtc = now };
        Apply(task, input, now);
        db.MinorTasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        return await GetOneAsync(db, task.Id, cancellationToken);
    }

    public async Task<MinorTaskSummary> UpdateAsync(int id, MinorTaskInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        await ValidateAsync(db, input, cancellationToken);
        var reminderBefore = task.ReminderAtUtc;
        Apply(task, input, UtcTime.Now(timeProvider));
        if (task.ReminderAtUtc != reminderBefore)
        {
            task.SnoozedUntilUtc = null; // a new reminder time starts fresh
        }

        await SaveIfChangedAsync(db, task, cancellationToken);
        return await GetOneAsync(db, id, cancellationToken);
    }

    public async Task<MinorTaskSummary> SetCompletedAsync(int id, bool completed, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        if (task.IsCompleted != completed)
        {
            task.IsCompleted = completed;
            task.CompletedAtUtc = completed ? UtcTime.Now(timeProvider) : null;
            task.SnoozedUntilUtc = null;
            await SaveIfChangedAsync(db, task, cancellationToken);
        }

        return await GetOneAsync(db, id, cancellationToken);
    }

    public async Task SnoozeAsync(int id, DateTime untilUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        task.SnoozedUntilUtc = UtcTime.Ensure(untilUtc, nameof(untilUtc));
        await SaveIfChangedAsync(db, task, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.MinorTasks.Remove(await FindAsync(db, id, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        var now = UtcTime.Now(timeProvider);
        if (Recurrence(task) is { } rule)
        {
            task.ReminderAtUtc = task.ReminderDisabled ? null : rule.NextUtc(now, timeProvider.LocalTimeZone);
        }
        else
        {
            task.IsCompleted = true;
            task.CompletedAtUtc = now;
        }

        task.SnoozedUntilUtc = null;
        await SaveIfChangedAsync(db, task, cancellationToken);
    }

    public async Task SetReminderDisabledAsync(int id, bool disabled, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        task.ReminderDisabled = disabled;
        task.SnoozedUntilUtc = null;
        if (disabled)
        {
            task.ReminderAtUtc = null;
        }
        else if (Recurrence(task) is { } rule)
        {
            task.ReminderAtUtc = rule.NextUtc(UtcTime.Now(timeProvider), timeProvider.LocalTimeZone);
        }

        await SaveIfChangedAsync(db, task, cancellationToken);
    }

    public async Task<IReadOnlyList<MinorTaskSummary>> GetDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var candidates = await db.MinorTasks.Where(m => !m.IsCompleted && m.ReminderAtUtc != null && m.ReminderAtUtc <= nowUtc
                && (m.SnoozedUntilUtc == null || m.SnoozedUntilUtc <= nowUtc))
            .ToListAsync(cancellationToken);
        var zone = timeProvider.LocalTimeZone;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone);
        var due = new List<int>();
        foreach (var task in candidates)
        {
            if (Recurrence(task) is { } rule && !rule.MayShow(nowLocal, TimeZoneInfo.ConvertTimeFromUtc(task.ReminderAtUtc!.Value, zone)))
            {
                // Missed outside the active period (closed, asleep, weekend): no late reminder, resume the schedule.
                task.ReminderAtUtc = rule.NextUtc(nowUtc, zone);
                task.SnoozedUntilUtc = null;
                task.ModifiedAtUtc = UtcTime.Now(timeProvider);
            }
            else
            {
                due.Add(task.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var result = await Summaries(db.MinorTasks.Where(m => due.Contains(m.Id))).ToListAsync(cancellationToken);
        return [.. result.OrderBy(m => m.ReminderAtUtc).ThenBy(m => m.Id)];
    }

    private static MinorTaskRecurrence? Recurrence(MinorTask m) => m.RepeatKind == MinorTaskRepeat.None ? null
        : new MinorTaskRecurrence(m.RepeatKind, m.RepeatIntervalMinutes, TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m.RepeatDailyMinute)), m.RepeatDays,
            TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m.ActiveFromMinute)), TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(m.ActiveUntilMinute)));

    private void Apply(MinorTask task, MinorTaskInput input, DateTime nowUtc)
    {
        task.Title = Text.Clean(input.Title)!;
        task.Description = Text.Clean(input.Description);
        task.ClientId = input.ClientId;
        var before = Recurrence(task);
        if (input.Recurrence is { Kind: not MinorTaskRepeat.None } rule)
        {
            task.RepeatKind = rule.Kind;
            task.RepeatIntervalMinutes = rule.IntervalMinutes;
            task.RepeatDailyMinute = (int)rule.DailyAt.ToTimeSpan().TotalMinutes;
            task.RepeatDays = rule.Days;
            task.ActiveFromMinute = (int)rule.ActiveFrom.ToTimeSpan().TotalMinutes;
            task.ActiveUntilMinute = (int)rule.ActiveUntil.ToTimeSpan().TotalMinutes;
            var next = task.ReminderDisabled ? null : rule.NextUtc(nowUtc, timeProvider.LocalTimeZone);
            if (task.ReminderAtUtc is not { } current || current > nowUtc || before != rule)
            {
                task.ReminderAtUtc = next; // keeps a due occurrence due when only the title or notes change
            }
        }
        else
        {
            task.RepeatKind = MinorTaskRepeat.None; // the schedule settings stay stored for when it is turned on again
            task.ReminderDisabled = false;
            task.ReminderAtUtc = input.ReminderAtUtc is { } at ? UtcTime.Ensure(at, nameof(input.ReminderAtUtc)) : null;
        }
    }

    private static async Task ValidateAsync(WorkTraceDbContext db, MinorTaskInput input, CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var title = Text.Clean(input.Title);
        if (title is null)
        {
            errors.Add(nameof(MinorTaskInput.Title), "Enter a title.");
        }
        else if (title.Length > MinorTask.TitleMaxLength)
        {
            errors.Add(nameof(MinorTaskInput.Title), $"Use at most {MinorTask.TitleMaxLength} characters.");
        }

        if (Text.Clean(input.Description)?.Length > MinorTask.DescriptionMaxLength)
        {
            errors.Add(nameof(MinorTaskInput.Description), $"Use at most {MinorTask.DescriptionMaxLength} characters.");
        }

        if (input.Recurrence is { Kind: not MinorTaskRepeat.None } rule)
        {
            if (rule.Days == 0)
            {
                errors.Add(nameof(MinorTaskInput.Recurrence), "Choose at least one weekday.");
            }
            else if (rule.Kind == MinorTaskRepeat.Interval
                && (rule.IntervalMinutes is < MinorTaskRecurrence.MinIntervalMinutes or > MinorTaskRecurrence.MaxIntervalMinutes))
            {
                errors.Add(nameof(MinorTaskInput.Recurrence), "Use an interval of 5 minutes to 24 hours.");
            }
            else if (rule.Kind == MinorTaskRepeat.Interval && rule.ActiveUntil <= rule.ActiveFrom)
            {
                errors.Add(nameof(MinorTaskInput.Recurrence), "The active hours must end after they start.");
            }
        }

        if (input.ClientId is { } clientId && !await db.Clients.AnyAsync(c => c.Id == clientId, cancellationToken))
        {
            errors.Add(nameof(MinorTaskInput.ClientId), "Choose an existing stakeholder.");
        }

        errors.ThrowIfInvalid();
    }

    private async Task SaveIfChangedAsync(WorkTraceDbContext db, MinorTask task, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        if (db.Entry(task).State == EntityState.Modified)
        {
            task.ModifiedAtUtc = UtcTime.Now(timeProvider);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<MinorTask> FindAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await db.MinorTasks.SingleOrDefaultAsync(m => m.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Minor task", id);

    private static async Task<MinorTaskSummary> GetOneAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await Summaries(db.MinorTasks.Where(m => m.Id == id)).SingleAsync(cancellationToken);

    private static IQueryable<MinorTaskSummary> Summaries(IQueryable<MinorTask> query) =>
        query.Select(m => new MinorTaskSummary(
            m.Id, m.Title, m.Description, m.ClientId, m.Client != null ? m.Client.Name : null,
            m.Client != null ? StakeholderColors.For(m.Client.Id, m.Client.Color) : null,
            m.ReminderAtUtc, m.SnoozedUntilUtc, m.IsCompleted, m.CompletedAtUtc, m.CreatedAtUtc,
            m.RepeatKind == MinorTaskRepeat.None ? null : new MinorTaskRecurrence(m.RepeatKind, m.RepeatIntervalMinutes,
                new TimeOnly(m.RepeatDailyMinute / 60, m.RepeatDailyMinute % 60), m.RepeatDays,
                new TimeOnly(m.ActiveFromMinute / 60, m.ActiveFromMinute % 60), new TimeOnly(m.ActiveUntilMinute / 60, m.ActiveUntilMinute % 60)),
            m.ReminderDisabled));
}
