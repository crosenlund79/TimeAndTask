using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Screenshots;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Screenshots;

/// <summary>Files under <c>Screenshots\yyyy\MM\dd\HHmmss.ext</c> (local date); content is never logged.</summary>
internal sealed class ScreenshotService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    AppPaths paths,
    ILogger<ScreenshotService> logger) : IScreenshotService
{
    public async Task<ScreenshotItem> SaveAsync(
        byte[] bytes, string extension, int width, int height, DateTime capturedAtUtc, CancellationToken cancellationToken = default)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(capturedAtUtc, TimeZoneInfo.Local);
        var relative = $"{local:yyyy}/{local:MM}/{local:dd}/{local:HHmmss}{extension}";
        var full = FullPath(relative);
        for (var n = 2; File.Exists(full); n++)
        {
            relative = $"{local:yyyy}/{local:MM}/{local:dd}/{local:HHmmss}-{n}{extension}";
            full = FullPath(relative);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes, cancellationToken);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var record = new ScreenshotRecord { CapturedAtUtc = capturedAtUtc, RelativePath = relative, Width = width, Height = height, SizeBytes = bytes.Length };
        db.Screenshots.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return new ScreenshotItem(record.Id, capturedAtUtc, full, width, height, bytes.Length, null);
    }

    public async Task<IReadOnlyList<ScreenshotItem>> GetDayAsync(DateOnly localDay, CancellationToken cancellationToken = default)
    {
        var (from, to) = DayRange(localDay);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var shots = await db.Screenshots.AsNoTracking().Where(s => s.CapturedAtUtc >= from && s.CapturedAtUtc < to)
            .OrderBy(s => s.CapturedAtUtc).ToListAsync(cancellationToken);
        var checkIns = await db.Observations.AsNoTracking().Where(o => !o.IsDeleted && o.ObservedAtUtc >= from && o.ObservedAtUtc < to)
            .OrderBy(o => o.ObservedAtUtc)
            .Select(o => new { o.ObservedAtUtc, Title = o.Task != null ? o.Task.Title : o.Note }).ToListAsync(cancellationToken);

        return [.. shots.Select(s => new ScreenshotItem(
            s.Id, s.CapturedAtUtc, FullPath(s.RelativePath), s.Width, s.Height, s.SizeBytes,
            checkIns.LastOrDefault(c => c.ObservedAtUtc <= s.CapturedAtUtc)?.Title))];
    }

    public async Task<DateOnly?> GetAdjacentDayAsync(DateOnly localDay, bool forward, CancellationToken cancellationToken = default)
    {
        var (from, to) = DayRange(localDay);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        DateTime? at = forward
            ? await db.Screenshots.Where(s => s.CapturedAtUtc >= to).MinAsync(s => (DateTime?)s.CapturedAtUtc, cancellationToken)
            : await db.Screenshots.Where(s => s.CapturedAtUtc < from).MaxAsync(s => (DateTime?)s.CapturedAtUtc, cancellationToken);
        return at is { } utc ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local)) : null;
    }

    public async Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.Screenshots.Where(s => ids.Contains(s.Id)).ToListAsync(cancellationToken);
        db.Screenshots.RemoveRange(records);
        await db.SaveChangesAsync(cancellationToken);
        records.ForEach(r => DeleteFile(r.RelativePath));
        logger.LogInformation("{Count} screenshot(s) deleted", records.Count);
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ids = await db.Screenshots.Where(s => s.CapturedAtUtc < cutoffUtc).Select(s => s.Id).ToListAsync(cancellationToken);
        if (ids.Count > 0)
        {
            await DeleteAsync(ids, cancellationToken);
        }

        return ids.Count;
    }

    public async Task<(int Count, long Bytes)> GetStorageAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.Screenshots.CountAsync(cancellationToken);
        var bytes = count == 0 ? 0 : await db.Screenshots.SumAsync(s => s.SizeBytes, cancellationToken);
        return (count, bytes);
    }

    private string FullPath(string relative) => Path.Combine(paths.ScreenshotsDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    private void DeleteFile(string relative)
    {
        try
        {
            File.Delete(FullPath(relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Screenshot file could not be deleted");
        }
    }

    private static (DateTime From, DateTime To) DayRange(DateOnly day) =>
        (TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local),
         TimeZoneInfo.ConvertTimeToUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local));
}
