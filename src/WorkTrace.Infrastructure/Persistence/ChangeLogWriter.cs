using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>
/// Captures before/after snapshots of <see cref="IChangeTracked"/> entities around a save and turns
/// them into one <see cref="ChangeLogEntry"/> batch.
/// </summary>
internal sealed class ChangeLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly List<(EntityEntry Entry, ChangeKind Kind, string? Before)> _pending = [];

    private ChangeLogWriter()
    {
    }

    public bool HasChanges => _pending.Count > 0;

    public static ChangeLogWriter Capture(ChangeTracker changeTracker)
    {
        changeTracker.DetectChanges();
        var writer = new ChangeLogWriter();
        foreach (var entry in changeTracker.Entries().Where(e => e.Entity is IChangeTracked))
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    writer._pending.Add((entry, ChangeKind.Insert, null));
                    break;
                case EntityState.Modified:
                    writer._pending.Add((entry, ChangeKind.Update, Serialize(entry.OriginalValues)));
                    break;
                case EntityState.Deleted:
                    writer._pending.Add((entry, ChangeKind.Delete, Serialize(entry.OriginalValues)));
                    break;
            }
        }

        return writer;
    }

    /// <summary>Builds log entries after the entity save, when generated keys are known.</summary>
    public IReadOnlyList<ChangeLogEntry> BuildEntries(Guid batchId, DateTime changedAtUtc) =>
        _pending.Select(p => new ChangeLogEntry
        {
            BatchId = batchId,
            EntityType = p.Entry.Metadata.ClrType.Name,
            EntityId = KeyOf(p.Entry, p.Kind),
            Kind = p.Kind,
            BeforeJson = p.Before,
            AfterJson = p.Kind == ChangeKind.Delete ? null : Serialize(p.Entry.CurrentValues),
            ChangedAtUtc = changedAtUtc,
        }).ToList();

    private static int KeyOf(EntityEntry entry, ChangeKind kind)
    {
        var key = entry.Metadata.FindPrimaryKey()!.Properties[0];
        var value = kind == ChangeKind.Delete ? entry.OriginalValues[key] : entry.CurrentValues[key];
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Serialize(PropertyValues values)
    {
        var dict = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in values.Properties)
        {
            dict[property.Name] = values[property];
        }

        return JsonSerializer.Serialize(dict, JsonOptions);
    }
}
