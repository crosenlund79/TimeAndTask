using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence.Configurations;

internal sealed class ClientConfiguration : IEntityTypeConfiguration<Client>
{
    public void Configure(EntityTypeBuilder<Client> builder)
    {
        builder.ToTable("Clients");
        builder.Property(c => c.Name).HasMaxLength(Client.NameMaxLength).UseCollation("NOCASE");
        builder.Property(c => c.Description).HasMaxLength(Client.DescriptionMaxLength);
        builder.HasIndex(c => c.Name).IsUnique();
    }
}
