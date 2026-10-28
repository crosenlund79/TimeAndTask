using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence.Configurations;

internal sealed class TaskSystemConfiguration : IEntityTypeConfiguration<TaskSystem>
{
    public void Configure(EntityTypeBuilder<TaskSystem> builder)
    {
        builder.ToTable("TaskSystems");
        LookupMapping.Configure(builder);
    }
}

internal sealed class CommunicationChannelConfiguration : IEntityTypeConfiguration<CommunicationChannel>
{
    public void Configure(EntityTypeBuilder<CommunicationChannel> builder)
    {
        builder.ToTable("CommunicationChannels");
        LookupMapping.Configure(builder);
    }
}

file static class LookupMapping
{
    public static void Configure<T>(EntityTypeBuilder<T> builder)
        where T : LookupItem
    {
        builder.Property(x => x.Name).HasMaxLength(LookupItem.NameMaxLength).UseCollation("NOCASE");
        builder.HasIndex(x => x.Name).IsUnique();
    }
}
