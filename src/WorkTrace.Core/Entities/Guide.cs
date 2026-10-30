namespace WorkTrace.Core.Entities;

/// <summary>A personal how-to guide: title, free-text category and plain-text content.</summary>
public class Guide : IAuditedEntity, IChangeTracked
{
    public const int TitleMaxLength = 200;
    public const int CategoryMaxLength = 100;

    public int Id { get; set; }

    public required string Title { get; set; }

    public string Category { get; set; } = "";

    public string Content { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Last change to title, category or content (drives the "stale" marker).</summary>
    public DateTime ModifiedAtUtc { get; set; }
}
