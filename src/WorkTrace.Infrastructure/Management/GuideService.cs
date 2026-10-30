using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Guides;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Management;

internal sealed class GuideService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    AppPaths paths,
    TimeProvider timeProvider,
    ILogger<GuideService> logger) : IGuideService
{
    public async Task<IReadOnlyList<GuideSummary>> SearchAsync(string? text, string? category, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var guides = await db.Guides.AsNoTracking().ToListAsync(cancellationToken);

        // A personal list is small: filter in memory so matching is accent-insensitive and covers the content.
        var words = Text.Clean(text)?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var cat = Text.Clean(category);
        return [.. guides
            .Where(g => cat is null || Text.SameName(g.Category, cat))
            .Where(g => words.All(w => Text.IndexOf(g.Title, w) >= 0 || Text.IndexOf(g.Category, w) >= 0 || Text.IndexOf(g.Content, w) >= 0))
            .OrderBy(g => g.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(g => g.Id)
            .Select(g => new GuideSummary(g.Id, g.Title, g.Category, g.CreatedAtUtc, g.ModifiedAtUtc))];
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var categories = await db.Guides.AsNoTracking().Select(g => g.Category).ToListAsync(cancellationToken);
        var distinct = new List<string>();
        foreach (var c in categories.Where(c => c.Length > 0).Order(StringComparer.CurrentCultureIgnoreCase))
        {
            if (!distinct.Any(d => Text.SameName(d, c)))
            {
                distinct.Add(c);
            }
        }

        return distinct;
    }

    public async Task<GuideDetail> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var guide = await db.Guides.AsNoTracking().SingleOrDefaultAsync(g => g.Id == id, cancellationToken)
            ?? throw new EntityNotFoundException("Guide", id);
        return await DetailAsync(db, guide, cancellationToken);
    }

    public async Task<GuideDetail> CreateAsync(string? title, string? category, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = UtcTime.Now(timeProvider);
        var guide = new Guide { Title = CleanTitle(title), Category = CleanCategory(category), CreatedAtUtc = now, ModifiedAtUtc = now };
        db.Guides.Add(guide);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Guide {GuideId} created", guide.Id);
        return await DetailAsync(db, guide, cancellationToken);
    }

    public async Task<GuideDetail> UpdateAsync(int id, string? title, string? category, string? content, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var guide = await db.Guides.SingleOrDefaultAsync(g => g.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Guide", id);
        guide.Title = CleanTitle(title);
        guide.Category = CleanCategory(category);
        guide.Content = (content ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
        db.ChangeTracker.DetectChanges();
        if (db.Entry(guide).State == EntityState.Modified)
        {
            guide.ModifiedAtUtc = UtcTime.Now(timeProvider);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await DetailAsync(db, guide, cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var guide = await db.Guides.SingleOrDefaultAsync(g => g.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Guide", id);
        var attachments = await db.GuideAttachments.Where(a => a.GuideId == id).ToListAsync(cancellationToken);
        db.GuideAttachments.RemoveRange(attachments);
        db.Guides.Remove(guide);
        await db.SaveChangesAsync(cancellationToken);
        attachments.ForEach(a => DeleteFile(a.RelativePath));
        logger.LogInformation("Guide {GuideId} deleted", id);
    }

    private static string CleanTitle(string? title)
    {
        var text = Text.Clean(title) ?? "Untitled guide";
        return text.Length > Guide.TitleMaxLength ? text[..Guide.TitleMaxLength] : text;
    }

    private static string CleanCategory(string? category)
    {
        var text = Text.Clean(category) ?? "";
        return text.Length > Guide.CategoryMaxLength ? text[..Guide.CategoryMaxLength] : text;
    }

    public async Task<GuideAttachmentItem> AddAttachmentAsync(int guideId, string sourcePath, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Guides.AnyAsync(g => g.Id == guideId, cancellationToken))
        {
            throw new EntityNotFoundException("Guide", guideId);
        }

        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            throw new OperationNotAllowedException($"{info.Name} was not found.");
        }

        var now = UtcTime.Now(timeProvider);
        var extension = Path.GetExtension(info.Name).ToLowerInvariant() is { Length: > 1 and <= 10 } ext && ext[1..].All(char.IsAsciiLetterOrDigit) ? ext : ".bin";
        var relative = $"guides/{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{extension}";
        var target = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        try
        {
            File.Copy(info.FullName, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationNotAllowedException($"{info.Name} could not be copied: {ex.Message}");
        }

        var name = info.Name.Length > GuideAttachment.FileNameMaxLength ? info.Name[^GuideAttachment.FileNameMaxLength..] : info.Name;
        var attachment = new GuideAttachment { GuideId = guideId, FileName = name, RelativePath = relative, SizeBytes = info.Length, CreatedAtUtc = now };
        db.GuideAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        return ToItem(attachment);
    }

    public async Task RemoveAttachmentAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.GuideAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new EntityNotFoundException("Attachment", attachmentId);
        db.GuideAttachments.Remove(attachment);
        await db.SaveChangesAsync(cancellationToken);
        DeleteFile(attachment.RelativePath);
    }

    private async Task<GuideDetail> DetailAsync(WorkTraceDbContext db, Guide g, CancellationToken cancellationToken)
    {
        var attachments = await db.GuideAttachments.AsNoTracking().Where(a => a.GuideId == g.Id).OrderBy(a => a.Id).ToListAsync(cancellationToken);
        return new(g.Id, g.Title, g.Category, g.Content, g.CreatedAtUtc, g.ModifiedAtUtc, [.. attachments.Select(ToItem)]);
    }

    private GuideAttachmentItem ToItem(GuideAttachment a) => new(a.Id, a.FileName, a.SizeBytes, a.CreatedAtUtc, FullPath(a.RelativePath));

    private string FullPath(string relative) => Path.Combine(paths.AttachmentsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    private void DeleteFile(string relative)
    {
        try
        {
            File.Delete(FullPath(relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Guide attachment file could not be deleted");
        }
    }
}
