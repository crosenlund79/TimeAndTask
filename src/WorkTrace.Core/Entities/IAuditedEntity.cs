namespace WorkTrace.Core.Entities;

/// <summary>Entities carrying creation/modification timestamps (UTC).</summary>
public interface IAuditedEntity
{
    DateTime CreatedAtUtc { get; set; }

    DateTime ModifiedAtUtc { get; set; }
}
