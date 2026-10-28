namespace WorkTrace.Core.Entities;

/// <summary>
/// Marker for user-editable entities whose inserts, updates and deletes are written to the
/// <see cref="ChangeLogEntry"/> audit log automatically when saved.
/// </summary>
public interface IChangeTracked;
