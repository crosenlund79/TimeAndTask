using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>
/// Application database. Saves of <see cref="IChangeTracked"/> entities automatically append a
/// <see cref="ChangeLogEntry"/> batch in the same transaction.
/// </summary>
public sealed class WorkTraceDbContext(DbContextOptions<WorkTraceDbContext> options, TimeProvider timeProvider) : DbContext(options)
{
    public DbSet<Client> Clients => Set<Client>();

    public DbSet<TaskSystem> TaskSystems => Set<TaskSystem>();

    public DbSet<CommunicationChannel> CommunicationChannels => Set<CommunicationChannel>();

    public DbSet<WorkTask> Tasks => Set<WorkTask>();

    public DbSet<Observation> Observations => Set<Observation>();

    public DbSet<CheckInPrompt> CheckInPrompts => Set<CheckInPrompt>();

    public DbSet<TrackingSegment> TrackingSegments => Set<TrackingSegment>();

    public DbSet<LockPeriod> LockPeriods => Set<LockPeriod>();

    public DbSet<ManualAllocation> ManualAllocations => Set<ManualAllocation>();

    public DbSet<TimesheetAdjustment> TimesheetAdjustments => Set<TimesheetAdjustment>();

    public DbSet<ScreenshotRecord> Screenshots => Set<ScreenshotRecord>();

    public DbSet<ChangeLogEntry> ChangeLog => Set<ChangeLogEntry>();

    public DbSet<BoardCard> BoardCards => Set<BoardCard>();

    public DbSet<BoardLink> BoardLinks => Set<BoardLink>();

    public DbSet<BoardComment> BoardComments => Set<BoardComment>();

    public DbSet<BoardCardEvent> BoardCardEvents => Set<BoardCardEvent>();

    public DbSet<BoardDependency> BoardDependencies => Set<BoardDependency>();

    public DbSet<BoardAttachment> BoardAttachments => Set<BoardAttachment>();

    public DbSet<BoardState> BoardState => Set<BoardState>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var changes = ChangeLogWriter.Capture(ChangeTracker);
        if (!changes.HasChanges)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        // Entity changes and their log entries commit atomically (or join the caller's transaction).
        var ownTransaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            ChangeLog.AddRange(changes.BuildEntries(Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime));
            await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            if (ownTransaction is not null)
            {
                await ownTransaction.CommitAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            if (ownTransaction is not null)
            {
                await ownTransaction.DisposeAsync();
            }
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

        // Enums are stored as integers (explicit values in Core) for compact, rename-safe storage.
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkTraceDbContext).Assembly);

        // Historical data must never be removed implicitly: no cascading deletes anywhere.
        foreach (var foreignKey in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()))
        {
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
        }
    }
}
