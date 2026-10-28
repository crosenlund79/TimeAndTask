using WorkTrace.Core.Common;

namespace WorkTrace.Core.Entities;

public enum ObservationOrigin
{
    Scheduled = 0,
    Manual = 1,
    Imported = 2,
}

/// <summary>
/// A point-in-time statement "at this moment I was working on X". An observation is evidence,
/// not a period: time allocations are inferred from observations by the estimation engine.
/// <para>
/// The <c>Original*</c> values are captured at creation and never change, so the original
/// statement always stays distinguishable from later corrections.
/// </para>
/// </summary>
public class Observation
{
    public const int NoteMaxLength = 4000;

    // EF Core materialization.
    private Observation()
    {
    }

    public int Id { get; private set; }

    public DateTime ObservedAtUtc { get; set; }

    /// <summary>When the record was actually created (may differ from <see cref="ObservedAtUtc"/>).</summary>
    public DateTime CreatedAtUtc { get; private set; }

    public ObservationOrigin Origin { get; private set; }

    public int? TaskId { get; set; }

    public WorkTask? Task { get; set; }

    public int? ChannelId { get; set; }

    public CommunicationChannel? Channel { get; set; }

    public string? Note { get; set; }

    /// <summary>Soft delete: the row and its original values are retained.</summary>
    public bool IsDeleted { get; set; }

    public DateTime ModifiedAtUtc { get; set; }

    public DateTime OriginalObservedAtUtc { get; private set; }

    public int? OriginalTaskId { get; private set; }

    public string? OriginalNote { get; private set; }

    /// <summary>True when any user-facing value differs from what was originally recorded.</summary>
    public bool IsCorrected =>
        IsDeleted
        || ObservedAtUtc != OriginalObservedAtUtc
        || TaskId != OriginalTaskId
        || !string.Equals(Note, OriginalNote, StringComparison.Ordinal);

    public static Observation Create(
        DateTime observedAtUtc,
        DateTime createdAtUtc,
        ObservationOrigin origin,
        int? taskId,
        int? channelId = null,
        string? note = null)
    {
        observedAtUtc = UtcTime.Ensure(observedAtUtc, nameof(observedAtUtc));
        createdAtUtc = UtcTime.Ensure(createdAtUtc, nameof(createdAtUtc));
        note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        return new Observation
        {
            ObservedAtUtc = observedAtUtc,
            CreatedAtUtc = createdAtUtc,
            ModifiedAtUtc = createdAtUtc,
            Origin = origin,
            TaskId = taskId,
            ChannelId = channelId,
            Note = note,
            OriginalObservedAtUtc = observedAtUtc,
            OriginalTaskId = taskId,
            OriginalNote = note,
        };
    }
}
