namespace WorkTrace.Core.Entities;

/// <summary>
/// A customer or internal cost bearer. Every task belongs to a client.
/// Clients are deactivated rather than deleted once referenced.
/// </summary>
public class Client : IAuditedEntity
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;

    /// <summary>Name of the client seeded on first start.</summary>
    public const string DefaultInternalName = "Internal";

    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool IsInternal { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }

    public List<WorkTask> Tasks { get; } = [];
}
