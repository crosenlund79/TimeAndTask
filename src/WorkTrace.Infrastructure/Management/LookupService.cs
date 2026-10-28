using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Management;

/// <summary>Maintains task systems and communication channels with identical rules.</summary>
internal sealed class LookupService(IDbContextFactory<WorkTraceDbContext> contextFactory) : ILookupService
{
    public async Task<IReadOnlyList<LookupSummary>> GetAsync(LookupKind kind, bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await Set(db, kind).Where(i => includeInactive || i.IsActive)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync(cancellationToken);
        var usage = await UsageAsync(db, kind, cancellationToken);
        return [.. items.Select(i => ToSummary(kind, i, usage))];
    }

    public async Task<LookupSummary> CreateAsync(LookupKind kind, string? name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var clean = await ValidateNameAsync(db, kind, name, existingId: null, cancellationToken);

        var set = Set(db, kind);
        var nextOrder = (await set.MaxAsync(i => (int?)i.SortOrder, cancellationToken) ?? 0) + 10;
        LookupItem item = kind == LookupKind.TaskSystem
            ? new TaskSystem { Name = clean, SortOrder = nextOrder }
            : new CommunicationChannel { Name = clean, SortOrder = nextOrder };
        db.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(kind, item, new Dictionary<int, int>());
    }

    public async Task<LookupSummary> RenameAsync(LookupKind kind, int id, string? name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await FindAsync(db, kind, id, cancellationToken);
        item.Name = await ValidateNameAsync(db, kind, name, id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(kind, item, await UsageAsync(db, kind, cancellationToken));
    }

    public async Task<LookupSummary> SetActiveAsync(LookupKind kind, int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await FindAsync(db, kind, id, cancellationToken);
        item.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(kind, item, await UsageAsync(db, kind, cancellationToken));
    }

    public async Task MoveAsync(LookupKind kind, int id, int direction, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var items = await Set(db, kind).OrderBy(i => i.SortOrder).ThenBy(i => i.Id).ToListAsync(cancellationToken);
        var index = items.FindIndex(i => i.Id == id);
        if (index < 0)
        {
            throw new EntityNotFoundException(Label(kind), id);
        }

        var target = index + Math.Sign(direction);
        if (direction == 0 || target < 0 || target >= items.Count)
        {
            return;
        }

        (items[index], items[target]) = (items[target], items[index]);
        for (var i = 0; i < items.Count; i++)
        {
            items[i].SortOrder = (i + 1) * 10;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(LookupKind kind, int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var item = await FindAsync(db, kind, id, cancellationToken);
        if (item.IsBuiltIn)
        {
            throw new OperationNotAllowedException($"'{item.Name}' is built in. You can rename or hide it instead.");
        }

        if ((await UsageAsync(db, kind, cancellationToken)).GetValueOrDefault(id) > 0)
        {
            throw new OperationNotAllowedException($"'{item.Name}' is in use and is kept for history. Hide it instead.");
        }

        db.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<string> ValidateNameAsync(WorkTraceDbContext db, LookupKind kind, string? name, int? existingId, CancellationToken cancellationToken)
    {
        var errors = ManagementValidator.ValidateLookupName(name);
        var clean = Text.Clean(name);
        if (clean is not null)
        {
            var names = await Set(db, kind).Where(i => i.Id != existingId).Select(i => i.Name).ToListAsync(cancellationToken);
            if (names.Any(n => Text.SameName(n, clean)))
            {
                errors.Add("Name", $"'{clean}' already exists.");
            }
        }

        errors.ThrowIfInvalid();
        return clean!;
    }

    private static IQueryable<LookupItem> Set(WorkTraceDbContext db, LookupKind kind) => kind switch
    {
        LookupKind.TaskSystem => db.TaskSystems,
        LookupKind.CommunicationChannel => db.CommunicationChannels,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static async Task<LookupItem> FindAsync(WorkTraceDbContext db, LookupKind kind, int id, CancellationToken cancellationToken) =>
        await Set(db, kind).SingleOrDefaultAsync(i => i.Id == id, cancellationToken)
        ?? throw new EntityNotFoundException(Label(kind), id);

    /// <summary>Number of records referencing each item (tasks; for channels also observations and allocations).</summary>
    private static async Task<Dictionary<int, int>> UsageAsync(WorkTraceDbContext db, LookupKind kind, CancellationToken cancellationToken)
    {
        var references = kind == LookupKind.TaskSystem
            ? await db.Tasks.Where(t => t.TaskSystemId != null).Select(t => t.TaskSystemId!.Value).ToListAsync(cancellationToken)
            : [
                .. await db.Tasks.Where(t => t.DefaultChannelId != null).Select(t => t.DefaultChannelId!.Value).ToListAsync(cancellationToken),
                .. await db.Observations.Where(o => o.ChannelId != null).Select(o => o.ChannelId!.Value).ToListAsync(cancellationToken),
                .. await db.ManualAllocations.Where(a => a.ChannelId != null).Select(a => a.ChannelId!.Value).ToListAsync(cancellationToken),
            ];
        return references.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
    }

    private static LookupSummary ToSummary(LookupKind kind, LookupItem item, IReadOnlyDictionary<int, int> usage) =>
        new(kind, item.Id, item.Name, item.SortOrder, item.IsActive, item.IsBuiltIn, usage.GetValueOrDefault(item.Id));

    private static string Label(LookupKind kind) => kind == LookupKind.TaskSystem ? "Task system" : "Communication channel";
}
