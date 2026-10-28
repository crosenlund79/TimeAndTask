using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Management;

internal sealed class ClientService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<ClientService> logger) : IClientService
{
    public async Task<IReadOnlyList<ClientSummary>> GetClientsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var clients = await Summaries(db.Clients.Where(c => includeInactive || c.IsActive)).ToListAsync(cancellationToken);
        return [.. clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<IReadOnlyList<ClientSummary>> GetClientsForPickerAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var clients = await Summaries(db.Clients.Where(c => c.IsActive)).ToListAsync(cancellationToken);
        return [.. clients
            .OrderByDescending(c => c.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public async Task<ClientSummary> CreateAsync(ClientInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateAsync(db, input, existingId: null, cancellationToken);

        var now = UtcTime.Now(timeProvider);
        var client = new Client
        {
            Name = Text.Clean(input.Name)!,
            Description = Text.Clean(input.Description),
            IsInternal = input.IsInternal,
            IsActive = true,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Client {ClientId} created", client.Id);
        return await GetSummaryAsync(db, client.Id, cancellationToken);
    }

    public async Task<ClientSummary> UpdateAsync(int id, ClientInput input, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var client = await FindAsync(db, id, cancellationToken);
        await ValidateAsync(db, input, id, cancellationToken);

        client.Name = Text.Clean(input.Name)!;
        client.Description = Text.Clean(input.Description);
        client.IsInternal = input.IsInternal;
        await SaveIfChangedAsync(db, client, cancellationToken);
        return await GetSummaryAsync(db, id, cancellationToken);
    }

    public async Task<ClientSummary> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var client = await FindAsync(db, id, cancellationToken);
        client.IsActive = isActive;
        await SaveIfChangedAsync(db, client, cancellationToken);
        logger.LogInformation("Client {ClientId} {State}", id, isActive ? "activated" : "deactivated");
        return await GetSummaryAsync(db, id, cancellationToken);
    }

    public async Task<bool> CanDeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return !await db.Tasks.AnyAsync(t => t.ClientId == id, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var client = await FindAsync(db, id, cancellationToken);
        if (await db.Tasks.AnyAsync(t => t.ClientId == id, cancellationToken))
        {
            throw new OperationNotAllowedException("This client has tasks and is kept for history. Deactivate it instead.");
        }

        if (client.IsInternal && await db.Clients.CountAsync(c => c.IsInternal && c.IsActive, cancellationToken) == 1 && client.IsActive)
        {
            throw new OperationNotAllowedException("Keep at least one active internal client for internal work.");
        }

        db.Clients.Remove(client);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Client {ClientId} deleted", id);
    }

    private async Task SaveIfChangedAsync(WorkTraceDbContext db, Client client, CancellationToken cancellationToken)
    {
        db.ChangeTracker.DetectChanges();
        if (db.Entry(client).State == EntityState.Modified)
        {
            client.ModifiedAtUtc = UtcTime.Now(timeProvider);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ValidateAsync(WorkTraceDbContext db, ClientInput input, int? existingId, CancellationToken cancellationToken)
    {
        var errors = ManagementValidator.Validate(input);
        var name = Text.Clean(input.Name);
        if (name is not null)
        {
            // Compared in .NET: SQLite NOCASE only folds ASCII letters.
            var names = await db.Clients.Where(c => c.Id != existingId).Select(c => c.Name).ToListAsync(cancellationToken);
            if (names.Any(n => Text.SameName(n, name)))
            {
                errors.Add(nameof(ClientInput.Name), $"A client named '{name}' already exists.");
            }
        }

        errors.ThrowIfInvalid();
    }

    private static async Task<Client> FindAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await db.Clients.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
        ?? throw new EntityNotFoundException("Client", id);

    private static async Task<ClientSummary> GetSummaryAsync(WorkTraceDbContext db, int id, CancellationToken cancellationToken) =>
        await Summaries(db.Clients.Where(c => c.Id == id)).SingleAsync(cancellationToken);

    private static IQueryable<ClientSummary> Summaries(IQueryable<Client> clients) =>
        clients.Select(c => new ClientSummary(
            c.Id,
            c.Name,
            c.Description,
            c.IsInternal,
            c.IsActive,
            c.Tasks.Count,
            c.Tasks.Count(t => !t.IsArchived),
            c.Tasks.Max(t => t.LastUsedAtUtc),
            c.ModifiedAtUtc));
}
