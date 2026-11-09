namespace WorkTrace.Core.Reporting;

/// <summary>
/// The one active interval while tracking: a task, a stakeholder without a task, or unclassified (both null; the next
/// selection registers it from <see cref="StartUtc"/>).
/// </summary>
public sealed record ActiveInterval(DateTime StartUtc, int? TaskId, int? ClientId)
{
    public bool IsUnclassified => TaskId is null && ClientId is null;
}

/// <summary>
/// User-confirmed changes to registered time. Everything is stored as manual allocations layered over the estimate
/// (the latest wins), so nothing is overwritten: check-ins and earlier registrations stay in the history, and the
/// timeline never gets overlapping intervals.
/// </summary>
public interface IRegistrationService
{
    /// <summary>Registers [start, end) on a task, or on a stakeholder without a task (one of the two is required).</summary>
    Task AssignAsync(DateTime startUtc, DateTime endUtc, int? taskId, int? clientId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a registration: the original range becomes unclassified, the new range is registered as given
    /// (it may be longer, shorter, moved or on another task).
    /// </summary>
    Task EditAsync(DateTime originalStartUtc, DateTime originalEndUtc, DateTime startUtc, DateTime endUtc, int? taskId, int? clientId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a registration: its range becomes unclassified (shown, never counted, classifiable later).</summary>
    Task DeleteAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default);

    /// <summary>The active interval at <paramref name="nowUtc"/>; null when not tracking (paused, asleep, closed).</summary>
    Task<ActiveInterval?> GetActiveAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
