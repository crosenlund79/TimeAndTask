namespace WorkTrace.Core.Entities;

/// <summary>
/// Shared shape of user-configurable lookup lists (task systems, communication channels).
/// </summary>
public abstract class LookupItem
{
    public const int NameMaxLength = 100;

    public int Id { get; set; }

    public required string Name { get; set; }

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Seeded by the application. Built-in items may be renamed or deactivated, not deleted.</summary>
    public bool IsBuiltIn { get; set; }
}
