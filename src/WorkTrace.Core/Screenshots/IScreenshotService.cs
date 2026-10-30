namespace WorkTrace.Core.Screenshots;

/// <summary>A stored capture. <see cref="Context"/> is the task of the latest check-in before it (same day), if any.</summary>
public sealed record ScreenshotItem(int Id, DateTime CapturedAtUtc, string FullPath, int Width, int Height, long SizeBytes, string? Context);

/// <summary>Automatic screenshot files (under the data directory) and their records.</summary>
public interface IScreenshotService
{
    Task<ScreenshotItem> SaveAsync(byte[] bytes, string extension, int width, int height, DateTime capturedAtUtc, CancellationToken cancellationToken = default);

    /// <summary>Captures of one local day, oldest first.</summary>
    Task<IReadOnlyList<ScreenshotItem>> GetDayAsync(DateOnly localDay, CancellationToken cancellationToken = default);

    /// <summary>The nearest day before/after <paramref name="localDay"/> that has captures, or null.</summary>
    Task<DateOnly?> GetAdjacentDayAsync(DateOnly localDay, bool forward, CancellationToken cancellationToken = default);

    Task DeleteAsync(IReadOnlyCollection<int> ids, CancellationToken cancellationToken = default);

    /// <summary>Retention: deletes captures older than <paramref name="cutoffUtc"/>; returns how many.</summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken = default);

    Task<(int Count, long Bytes)> GetStorageAsync(CancellationToken cancellationToken = default);
}
