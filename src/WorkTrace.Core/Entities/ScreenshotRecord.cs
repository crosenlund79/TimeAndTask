namespace WorkTrace.Core.Entities;

/// <summary>Metadata for a screenshot file. Image data lives on disk only.</summary>
public class ScreenshotRecord
{
    public const int RelativePathMaxLength = 400;

    public int Id { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    /// <summary>Path relative to the screenshot root, using '/' separators.</summary>
    public required string RelativePath { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public long SizeBytes { get; set; }
}
