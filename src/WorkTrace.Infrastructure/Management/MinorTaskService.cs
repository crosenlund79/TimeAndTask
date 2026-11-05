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
        Apply(task, input);
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
        Apply(task, input);
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

    public async Task<IReadOnlyList<MinorTaskSummary>> GetDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var due = await Summaries(db.MinorTasks.Where(m => !m.IsCompleted && m.ReminderAtUtc != null && m.ReminderAtUtc <= nowUtc
                && (m.SnoozedUntilUtc == null || m.SnoozedUntilUtc <= nowUtc)))
            .ToListAsync(cancellationToken);
        return [.. due.OrderBy(m => m.ReminderAtUtc).ThenBy(m => m.Id)];
    }

    private static void Apply(MinorTask task, MinorTaskInput input)
    {
        task.Title = Text.Clean(input.Title)!;
        task.Description = Text.Clean(input.Description);
        task.ClientId = input.ClientId;
        task.ReminderAtUtc = input.ReminderAtUtc is { } at ? UtcTime.Ensure(at, nameof(input.ReminderAtUtc)) : null;
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
            m.ReminderAtUtc, m.SnoozedUntilUtc, m.IsCompleted, m.CompletedAtUtc, m.CreatedAtUtc));
}
