using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence.Configurations;

internal sealed class BoardCardConfiguration : IEntityTypeConfiguration<BoardCard>
{
    public void Configure(EntityTypeBuilder<BoardCard> builder)
    {
        builder.ToTable("BoardCards");
        builder.Ignore(c => c.Key);
        builder.Property(c => c.Uid).HasMaxLength(BoardCard.UidMaxLength);
        builder.Property(c => c.Title).HasMaxLength(BoardCard.TitleMaxLength);
        builder.HasIndex(c => c.Number).IsUnique();
        builder.HasIndex(c => c.Uid).IsUnique();
        builder.HasIndex(c => new { c.Status, c.SortOrder });

        builder.HasOne(c => c.WorkTask).WithMany().HasForeignKey(c => c.WorkTaskId);
        builder.HasMany(c => c.Links).WithOne().HasForeignKey(l => l.CardId);
        builder.HasMany(c => c.Comments).WithOne().HasForeignKey(l => l.CardId);
        builder.HasMany(c => c.Events).WithOne().HasForeignKey(l => l.CardId);
        builder.HasMany(c => c.Attachments).WithOne().HasForeignKey(l => l.CardId);
        builder.HasMany(c => c.BlockedBy).WithOne().HasForeignKey(d => d.CardId);
    }
}

internal sealed class BoardLinkConfiguration : IEntityTypeConfiguration<BoardLink>
{
    public void Configure(EntityTypeBuilder<BoardLink> builder)
    {
        builder.ToTable("BoardLinks");
        builder.Property(l => l.Label).HasMaxLength(BoardLink.LabelMaxLength);
        builder.Property(l => l.Url).HasMaxLength(BoardLink.UrlMaxLength);
    }
}

internal sealed class BoardCommentConfiguration : IEntityTypeConfiguration<BoardComment>
{
    public void Configure(EntityTypeBuilder<BoardComment> builder)
    {
        builder.ToTable("BoardComments");
        builder.Property(c => c.Uid).HasMaxLength(BoardCard.UidMaxLength);
        builder.Property(c => c.Author).HasMaxLength(BoardComment.AuthorMaxLength);
        builder.HasIndex(c => c.Uid).IsUnique();
    }
}

internal sealed class BoardCardEventConfiguration : IEntityTypeConfiguration<BoardCardEvent>
{
    public void Configure(EntityTypeBuilder<BoardCardEvent> builder)
    {
        builder.ToTable("BoardCardEvents");
        builder.Property(e => e.Uid).HasMaxLength(BoardCard.UidMaxLength);
        builder.Property(e => e.By).HasMaxLength(BoardComment.AuthorMaxLength);
        builder.Property(e => e.Note).HasMaxLength(2000);
        builder.HasIndex(e => e.Uid).IsUnique();
    }
}

internal sealed class BoardDependencyConfiguration : IEntityTypeConfiguration<BoardDependency>
{
    public void Configure(EntityTypeBuilder<BoardDependency> builder)
    {
        builder.ToTable("BoardDependencies");
        builder.HasOne(d => d.BlockedByCard).WithMany().HasForeignKey(d => d.BlockedByCardId);
        builder.HasIndex(d => new { d.CardId, d.BlockedByCardId }).IsUnique();
        builder.ToTable(t => t.HasCheckConstraint("CK_BoardDependencies_NotSelf", "CardId <> BlockedByCardId"));
    }
}

internal sealed class BoardAttachmentConfiguration : IEntityTypeConfiguration<BoardAttachment>
{
    public void Configure(EntityTypeBuilder<BoardAttachment> builder)
    {
        builder.ToTable("BoardAttachments");
        builder.Property(a => a.Uid).HasMaxLength(BoardCard.UidMaxLength);
        builder.Property(a => a.RelativePath).HasMaxLength(ScreenshotRecord.RelativePathMaxLength);
        builder.Property(a => a.OriginalName).HasMaxLength(BoardAttachment.NameMaxLength);
        builder.Property(a => a.Caption).HasMaxLength(BoardAttachment.CaptionMaxLength);
        builder.Property(a => a.ContentType).HasMaxLength(100);
        builder.Property(a => a.Sha256).HasMaxLength(64);
        builder.HasIndex(a => a.Uid).IsUnique();
    }
}

internal sealed class BoardStateConfiguration : IEntityTypeConfiguration<BoardState>
{
    public void Configure(EntityTypeBuilder<BoardState> builder)
    {
        builder.ToTable("BoardState");
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasData(new BoardState { Id = BoardState.SingletonId, NextNumber = 1 });
    }
}
