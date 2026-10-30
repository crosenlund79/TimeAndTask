namespace WorkTrace.Core.Management;

/// <summary>Client maintenance. Clients referenced by tasks are deactivated, never deleted.</summary>
public interface IClientService
{
    Task<IReadOnlyList<ClientSummary>> GetClientsAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    /// <summary>Active clients ordered for pickers: most recently used first, then alphabetically.</summary>
    Task<IReadOnlyList<ClientSummary>> GetClientsForPickerAsync(CancellationToken cancellationToken = default);

    Task<ClientSummary> CreateAsync(ClientInput input, CancellationToken cancellationToken = default);

    Task<ClientSummary> UpdateAsync(int id, ClientInput input, CancellationToken cancellationToken = default);

    Task<ClientSummary> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>True when the client has no tasks and can therefore be deleted permanently.</summary>
    Task<bool> CanDeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <exception cref="Validation.OperationNotAllowedException">The client is referenced.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The stakeholder's tasks, and how many of them have recorded time (those block deletion).</summary>
    Task<StakeholderUsage> GetUsageAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Deletes the stakeholder together with its tasks, none of which may have recorded time.</summary>
    /// <exception cref="Validation.OperationNotAllowedException">A task has recorded time.</exception>
    Task DeleteWithUnusedTasksAsync(int id, CancellationToken cancellationToken = default);
}

public sealed record StakeholderUsage(int Tasks, int TasksWithTime)
{
    public bool CanDeleteWithTasks => TasksWithTime == 0;
}

/// <summary>Reusable work items. Tasks referenced by time records are archived, never deleted.</summary>
public interface ITaskService
{
    /// <summary>Search/autocomplete: see <see cref="TaskSearch"/> for ranking rules.</summary>
    Task<IReadOnlyList<TaskSummary>> SearchAsync(TaskQuery query, CancellationToken cancellationToken = default);

    Task<TaskSummary> GetAsync(int id, CancellationToken cancellationToken = default);

    Task<TaskSummary> CreateAsync(TaskInput input, CancellationToken cancellationToken = default);

    Task<TaskSummary> UpdateAsync(int id, TaskInput input, CancellationToken cancellationToken = default);

    Task<TaskSummary> SetArchivedAsync(int id, bool isArchived, CancellationToken cancellationToken = default);

    /// <summary>
    /// Other tasks of the same client with the same task system and external ID. External IDs are not
    /// unique, so this is a warning for the user, not an error.
    /// </summary>
    Task<IReadOnlyList<TaskSummary>> FindSimilarAsync(TaskInput input, int? excludeTaskId = null, CancellationToken cancellationToken = default);

    Task<bool> CanDeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <exception cref="Validation.OperationNotAllowedException">The task is referenced by time records.</exception>
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Records that the task was just used (drives "recent" ordering).</summary>
    Task MarkUsedAsync(int id, CancellationToken cancellationToken = default);
}

/// <summary>Configurable lists: task systems and communication channels.</summary>
public interface ILookupService
{
    Task<IReadOnlyList<LookupSummary>> GetAsync(LookupKind kind, bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<LookupSummary> CreateAsync(LookupKind kind, string? name, CancellationToken cancellationToken = default);

    Task<LookupSummary> RenameAsync(LookupKind kind, int id, string? name, CancellationToken cancellationToken = default);

    Task<LookupSummary> SetActiveAsync(LookupKind kind, int id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Moves an item one position up (-1) or down (+1); renumbers the list.</summary>
    Task MoveAsync(LookupKind kind, int id, int direction, CancellationToken cancellationToken = default);

    /// <exception cref="Validation.OperationNotAllowedException">Built-in or in use.</exception>
    Task DeleteAsync(LookupKind kind, int id, CancellationToken cancellationToken = default);
}
