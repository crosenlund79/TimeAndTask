namespace WorkTrace.Core.Reporting;

/// <summary>An outstanding stretch of unclassified tracked time ending now (since start-up, a missed check-in, ...).</summary>
public sealed record OpenInterval(DateTime StartUtc, DateTime EndUtc)
{
    public TimeSpan Duration => EndUtc - StartUtc;
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

    /// <summary>The unclassified time just before <paramref name="nowUtc"/> in the current tracking session, if any (at least a minute).</summary>
    Task<OpenInterval?> GetOpenIntervalAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
