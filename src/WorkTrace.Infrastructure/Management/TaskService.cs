using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Management;

internal sealed class TaskService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    TimeProvider timeProvider,
    IBoardService board,
    ISettingsStore settings,
    ILogger<TaskService> logger) : ITaskService
{
    public async Task<IReadOnlyList<TaskSummary>> SearchAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var tasks = db.Tasks.AsQueryable();
        if (query.ClientId is { } clientId)
        {
            tasks = tasks.Where(t => t.ClientId == clientId);
        }

        if (!query.IncludeArchived)
        {
            tasks = tasks.Where(t => !t.IsArchived);
        }

        if (!query.IncludeInactiveClients && query.ClientId is null)
        {
            tasks = tasks.Where(t => t.Client!.IsActive);
        }

        // A personal task list is small (hundreds to a few thousand rows): rank in memory so matching
        // can be accent-insensitive and deterministic, which SQLite LIKE cannot provide.
        var candidates = await Summaries(db, tasks).ToListAsync(cancellationToken);
        return TaskSearch.Rank(candidates, query.Text, UtcTime.Now(timeProvider), query.Limit);
    }

    public async Task<TaskSummary> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(db, db.Tasks.Where(t => t.Id == id)).SingleOrDefaultAsync(cancellationToken)
            ?? throw new EntityNotFoundException("Task", id);
    }

    public async Task<TaskSummary> CreateAsync(TaskInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateAsync(db, input, existing: null, cancellationToken);

        var now = UtcTime.Now(timeProvider);
        var task = new WorkTask
        {
            ClientId = input.ClientId!.Value,
            Title = Text.Clean(input.Title)!,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
        };
        Apply(task, input);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Task {TaskId} created", task.Id);
        if (input.ShowOnBoard ?? settings.Current.Board.AutoAddTasks)
        {
            await BoardAsync(task.Id, b => b.SetTaskOnBoardAsync(task.Id, true, cancellationToken));
        }

        return await GetAsync(task.Id, cancellationToken);
    }

    public async Task<TaskSummary> UpdateAsync(int id, TaskInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        await ValidateAsync(db, input, task, cancellationToken);

        Apply(task, input);
        await SaveIfChangedAsync(db, task, cancellationToken);
        await BoardAsync(id, b => b.SyncTaskTitleAsync(id, cancellationToken));
        if (input.ShowOnBoard is { } onBoard)
        {
            await BoardAsync(id, b => b.SetTaskOnBoardAsync(id, onBoard, cancellationToken));
        }

        return await GetAsync(id, cancellationToken);
    }

    public async Task<TaskSummary> SetArchivedAsync(int id, bool isArchived, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        if (!isArchived && !await db.Clients.AnyAsync(c => c.Id == task.ClientId && c.IsActive, cancellationToken))
        {
            throw new OperationNotAllowedException("The task's stakeholder is inactive. Reactivate the stakeholder first.");
        }

        task.IsArchived = isArchived;
        await SaveIfChangedAsync(db, task, cancellationToken);
        if (isArchived)
        {
            await BoardAsync(id, b => b.SetTaskOnBoardAsync(id, false, cancellationToken));
        }

        logger.LogInformation("Task {TaskId} {State}", id, isArchived ? "archived" : "restored");
        return await GetAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<TaskSummary>> FindSimilarAsync(TaskInput input, int? excludeTaskId = null, CancellationToken cancellationToken = default)
    {
        var externalId = Text.Clean(input.ExternalId);
        if (input.ClientId is null || externalId is null)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sameClient = await Summaries(db, db.Tasks.Where(t =>
                t.ClientId == input.ClientId && t.TaskSystemId == input.TaskSystemId && t.Id != excludeTaskId && t.ExternalId != null))
            .ToListAsync(cancellationToken);
        return [.. sameClient.Where(t => Text.SameName(t.ExternalId, externalId)).OrderBy(t => t.Id)];
    }

    public async Task<bool> CanDeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return !await IsReferencedAsync(db, id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await FindAsync(db, id, cancellationToken);
        if (await IsReferencedAsync(db, id, cancellationToken))
        {
            throw new OperationNotAllowedException("This task has recorded time and is kept for history. Archive it instead.");
        }

        // Its board card (if any) stays as an archived, unlinked card.
        await BoardAsync(id, b => b.SetTaskOnBoardAsync(id, false, cancellationToken));
        await db.BoardCards.Where(c => c.WorkTaskId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.WorkTaskId, (int?)null), cancellationToken);
        db.Tasks.Remove(task);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Task {TaskId} deleted", id);
    }

    public async Task MarkUsedAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcTime.Now(timeProvider);

        // Usage is not a user edit: update directly without touching ModifiedAt or the change log.
        var updated = await db.Tasks.Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAtUtc, now), cancellationToken);
        if (updated == 0)
        {
            throw new EntityNotFoundException("Task", id);
        }
    }

    /// <summary>The Board is optional: a failure there is logged and never breaks time tracking.</summary>
    private async Task BoardAsync(int taskId, Func<IBoardService, Task> action)
    {
        try
        {
            await action(board);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Board card of task {TaskId} could not be updated", taskId);
        }
    }

    private static void Apply(WorkTask task, TaskInput input)
    {
        task.ClientId = input.ClientId!.Value;
        task.TaskSystemId = input.TaskSystemId;
        task.ExternalId = Text.Clean(input.ExternalId);
        task.Title = Text.Clean(input.Title)!;
        task.Notes = Text.Clean(input.Notes);
        task.DefaultChannelId = input.DefaultChannelId;
    }

    private async Task SaveIfChangedAsync(WorkTraceDbContext db, WorkTask task, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        if (db.Entry(task).State == EntityState.Modified)
        {
            task.ModifiedAtUtc = UtcTime.Now(timeProvider);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// References must exist; newly chosen references must also be active. Keeping an existing
    /// (now inactive) reference on edit is allowed so old tasks stay editable.
    /// </summary>
    private static async Task ValidateAsync(WorkTraceDbContext db, TaskInput input, WorkTask? existing, CancellationToken cancellationToken)
    {
        var errors = ManagementValidator.Validate(input);

        if (input.ClientId is { } clientId)
        {
            var client = await db.Clients.Where(c => c.Id == clientId).Select(c => new { c.IsActive }).SingleOrDefaultAsync(cancellationToken);
            if (client is null)
            {
                errors.Add(nameof(TaskInput.ClientId), "The selected stakeholder no longer exists.");
            }
            else if (!client.IsActive && existing?.ClientId != clientId)
            {
                errors.Add(nameof(TaskInput.ClientId), "The selected stakeholder is inactive.");
            }
        }

        if (input.TaskSystemId is { } systemId)
        {
            var system = await db.TaskSystems.Where(s => s.Id == systemId).Select(s => new { s.IsActive }).SingleOrDefaultAsync(cancellationToken);
            if (system is null || (!system.IsActive && existing?.TaskSystemId != systemId))
            {
                errors.Add(nameof(TaskInput.TaskSystemId), "Choose an active task system.");
            }
        }

        if (input.DefaultChannelId is { } channelId)
        {
            var channel = await db.CommunicationChannels.Where(c => c.Id == channelId).Select(c => new { c.IsActive }).SingleOrDefaultAsync(cancellationToken);
            if (channel is null || (!channel.IsActive && existing?.DefaultChannelId != channelId))
            {
                errors.Add(nameof(TaskInput.DefaultChannelId), "Choose an active communication channel.");
            }
        }

        errors.ThrowIfInvalid();
    }

    private static async Task<bool> IsReferencedAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await db.Observations.AnyAsync(o => o.TaskId == id || o.OriginalTaskId == id, cancellationToken)
        || await db.ManualAllocations.AnyAsync(a => a.TaskId == id, cancellationToken)
        || await db.TimesheetAdjustments.AnyAsync(a => a.TaskId == id, cancellationToken);

    private static async Task<WorkTask> FindAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await db.Tasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken)
        ?? throw new EntityNotFoundException("Task", id);

    private static IQueryable<TaskSummary> Summaries(WorkTraceDbContext db, IQueryable<WorkTask> tasks) =>
        tasks.Select(t => new TaskSummary(
            t.Id,
            t.ClientId,
            t.Client!.Name,
            t.Client.IsActive,
            t.TaskSystemId,
            t.TaskSystem != null ? t.TaskSystem.Name : null,
            t.ExternalId,
            t.Title,
            t.Notes,
            t.DefaultChannelId,
            t.DefaultChannel != null ? t.DefaultChannel.Name : null,
            t.IsArchived,
            t.CreatedAtUtc,
            t.ModifiedAtUtc,
            t.LastUsedAtUtc,
            db.BoardCards.Any(c => c.WorkTaskId == t.Id && !c.IsArchived)));
}
