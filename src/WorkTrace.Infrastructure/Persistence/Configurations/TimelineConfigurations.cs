using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence.Configurations;

internal sealed class ObservationConfiguration : IEntityTypeConfiguration<Observation>
{
    public void Configure(EntityTypeBuilder<Observation> builder)
    {
        builder.ToTable("Observations");
        builder.Property(o => o.Note).HasMaxLength(Observation.NoteMaxLength);
        builder.Property(o => o.OriginalNote).HasMaxLength(Observation.NoteMaxLength);
        builder.Ignore(o => o.IsCorrected);

        builder.HasOne(o => o.Task).WithMany().HasForeignKey(o => o.TaskId);
        builder.HasOne<WorkTask>().WithMany().HasForeignKey(o => o.OriginalTaskId);
        builder.HasOne(o => o.Channel).WithMany().HasForeignKey(o => o.ChannelId);

        builder.HasIndex(o => o.ObservedAtUtc);
    }
}

internal sealed class CheckInPromptConfiguration : IEntityTypeConfiguration<CheckInPrompt>
{
    public void Configure(EntityTypeBuilder<CheckInPrompt> builder)
    {
        builder.ToTable("CheckInPrompts");
        builder.Ignore(p => p.CreatesUncertainty);
        builder.HasOne(p => p.Observation).WithMany().HasForeignKey(p => p.ObservationId);
        builder.HasIndex(p => p.ScheduledAtUtc);
    }
}

internal sealed class TrackingSegmentConfiguration : IEntityTypeConfiguration<TrackingSegment>
{
    public void Configure(EntityTypeBuilder<TrackingSegment> builder)
    {
        builder.ToTable("TrackingSegments");
        builder.Ignore(s => s.IsOpen);
        builder.HasIndex(s => s.StartUtc);
        builder.ToTable(t => t.HasCheckConstraint("CK_TrackingSegments_Range", "EndUtc IS NULL OR EndUtc >= StartUtc"));
    }
}

internal sealed class LockPeriodConfiguration : IEntityTypeConfiguration<LockPeriod>
{
    public void Configure(EntityTypeBuilder<LockPeriod> builder)
    {
        builder.ToTable("LockPeriods");
        builder.HasIndex(l => l.StartUtc);
        builder.ToTable(t => t.HasCheckConstraint("CK_LockPeriods_Range", "EndUtc IS NULL OR EndUtc >= StartUtc"));
    }
}

internal sealed class ManualAllocationConfiguration : IEntityTypeConfiguration<ManualAllocation>
{
    public void Configure(EntityTypeBuilder<ManualAllocation> builder)
    {
        builder.ToTable("ManualAllocations");
        builder.Property(a => a.Note).HasMaxLength(ManualAllocation.NoteMaxLength);
        builder.HasOne(a => a.Task).WithMany().HasForeignKey(a => a.TaskId);
        builder.HasOne(a => a.Channel).WithMany().HasForeignKey(a => a.ChannelId);
        builder.HasIndex(a => new { a.StartUtc, a.EndUtc });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ManualAllocations_Range", "EndUtc > StartUtc");
            t.HasCheckConstraint(
                "CK_ManualAllocations_Task",
                $"(Kind = {(int)AllocationKind.Task} AND TaskId IS NOT NULL) OR (Kind <> {(int)AllocationKind.Task} AND TaskId IS NULL)");
        });
    }
}

internal sealed class TimesheetAdjustmentConfiguration : IEntityTypeConfiguration<TimesheetAdjustment>
{
    public void Configure(EntityTypeBuilder<TimesheetAdjustment> builder)
    {
        builder.ToTable("TimesheetAdjustments");
        builder.Property(a => a.Note).HasMaxLength(TimesheetAdjustment.NoteMaxLength);
        builder.HasOne(a => a.Task).WithMany().HasForeignKey(a => a.TaskId);
        builder.HasIndex(a => new { a.LocalDate, a.TaskId }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_TimesheetAdjustments_Minutes", "AdjustedMinutes >= 0 AND BaselineMinutes >= 0"));
    }
}

internal sealed class ScreenshotRecordConfiguration : IEntityTypeConfiguration<ScreenshotRecord>
{
    public void Configure(EntityTypeBuilder<ScreenshotRecord> builder)
    {
        builder.ToTable("Screenshots");
        builder.Property(s => s.RelativePath).HasMaxLength(ScreenshotRecord.RelativePathMaxLength);
        builder.HasIndex(s => s.RelativePath).IsUnique();
        builder.HasIndex(s => s.CapturedAtUtc);
    }
}

internal sealed class ChangeLogEntryConfiguration : IEntityTypeConfiguration<ChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<ChangeLogEntry> builder)
    {
        builder.ToTable("ChangeLog");
        builder.Property(c => c.EntityType).HasMaxLength(ChangeLogEntry.EntityTypeMaxLength);
        builder.HasIndex(c => c.BatchId);
        builder.HasIndex(c => new { c.EntityType, c.EntityId });
        builder.HasIndex(c => c.ChangedAtUtc);
    }
}
