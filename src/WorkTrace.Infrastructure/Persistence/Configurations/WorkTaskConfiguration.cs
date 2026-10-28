using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence.Configurations;

internal sealed class WorkTaskConfiguration : IEntityTypeConfiguration<WorkTask>
{
    public void Configure(EntityTypeBuilder<WorkTask> builder)
    {
        builder.ToTable("Tasks");
        builder.Property(t => t.Title).HasMaxLength(WorkTask.TitleMaxLength);
        builder.Property(t => t.ExternalId).HasMaxLength(WorkTask.ExternalIdMaxLength).UseCollation("NOCASE");
        builder.Property(t => t.Notes).HasMaxLength(WorkTask.NotesMaxLength);

        builder.HasOne(t => t.Client).WithMany(c => c.Tasks).HasForeignKey(t => t.ClientId);
        builder.HasOne(t => t.TaskSystem).WithMany().HasForeignKey(t => t.TaskSystemId);
        builder.HasOne(t => t.DefaultChannel).WithMany().HasForeignKey(t => t.DefaultChannelId);

        // External IDs are not unique, but lookups by (client, system, id) must be fast.
        builder.HasIndex(t => new { t.ClientId, t.TaskSystemId, t.ExternalId });
        builder.HasIndex(t => t.LastUsedAtUtc);
    }
}
