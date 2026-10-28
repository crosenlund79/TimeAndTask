using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence;

public sealed class WorkTraceDbContext(DbContextOptions<WorkTraceDbContext> options) : DbContext(options)
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
