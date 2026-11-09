using WorkTrace.Core.Settings;

namespace WorkTrace.Core.Reporting;

public enum PeriodKind
{
    /// <summary>Explicitly allocated by the user: the only confirmed time.</summary>
    Manual = 0,

    /// <summary>Inferred from check-ins; always shown as estimated.</summary>
    Estimated = 1,

    /// <summary>Tracking was on, but there is no evidence what was worked on.</summary>
    Unclassified = 2,

    /// <summary>Paused, asleep, shut down (or locked with "locked time is not work"). Never filled.</summary>
    NotTracking = 3,

    /// <summary>Screen locked while tracking (default handling): not work, shown as locked, classifiable; the active task continues afterwards.</summary>
    Locked = 4,
}

/// <summary>A half-open interval [Start, End). <see cref="TaskId"/> is null for non-task time (note-only check-ins, non-work allocations).</summary>
/// <param name="ClientId">Stakeholder of time registered without a task.</param>
public sealed record TimelinePeriod(DateTime StartUtc, DateTime EndUtc, PeriodKind Kind, int? TaskId, bool IsNonWork = false, int? ClientId = null)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <param name="ContinuesActive">
/// The segment follows one that ended without an explicit stop (sleep, clock change) in the same application run: the
/// task active at that end is still the active task. After a pause, exit or crash a segment starts unclassified.
/// </param>
public sealed record EstimationSegment(DateTime StartUtc, DateTime EndUtc, bool ContinuesActive = false);

public sealed record EstimationObservation(int Id, DateTime AtUtc, DateTime CreatedAtUtc, int? TaskId, int? ClientId = null);

/// <param name="ClientId">Stakeholder of time registered without a task.</param>
/// <param name="IsCleared">A deleted registration: the range becomes unclassified.</param>
public sealed record EstimationAllocation(int Id, DateTime StartUtc, DateTime EndUtc, DateTime CreatedAtUtc, int? TaskId, bool IsNonWork,
    int? ClientId = null, bool IsCleared = false);

public sealed record EstimationInput(
    IReadOnlyList<EstimationSegment> Segments,
    IReadOnlyList<EstimationSegment> Locks,
    IReadOnlyList<EstimationObservation> Observations,
    IReadOnlyList<EstimationAllocation> Allocations,
    LockedTimeHandling LockHandling,
    TimeSpan LockGrace);

/// <summary>
/// Reconstructs a timeline from check-ins (docs/architecture.md, "Time-estimation algorithm"). Pure and deterministic:
/// nothing derived is stored, so edits to check-ins or allocations are reflected the next time it runs.
/// </summary>
public static class TimeEstimator
{
    /// <summary>Flat, ordered, non-overlapping periods covering [<paramref name="fromUtc"/>, <paramref name="toUtc"/>).</summary>
    public static IReadOnlyList<TimelinePeriod> Estimate(EstimationInput input, DateTime fromUtc, DateTime toUtc)
    {
        ArgumentNullException.ThrowIfNull(input);
        var periods = new List<TimelinePeriod>();
        EstimationObservation? carried = null;
        foreach (var segment in input.Segments.Where(s => s.EndUtc > s.StartUtc).OrderBy(s => s.StartUtc))
        {
            var (segmentPeriods, activeAtEnd) = EstimateSegment(input, segment, segment.ContinuesActive ? carried : null);
            periods.AddRange(segmentPeriods);
            carried = activeAtEnd;
        }

        // Manual allocations overlay everything (latest created wins), also outside tracking.
        foreach (var allocation in input.Allocations.Where(a => a.EndUtc > a.StartUtc).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id))
        {
            periods = Paint(periods, allocation.IsCleared
                ? new TimelinePeriod(allocation.StartUtc, allocation.EndUtc, PeriodKind.Unclassified, null)
                : new TimelinePeriod(allocation.StartUtc, allocation.EndUtc, PeriodKind.Manual, allocation.TaskId, allocation.IsNonWork,
                    allocation.TaskId is null ? allocation.ClientId : null));
        }

        return Flatten(periods, fromUtc, toUtc);
    }

    /// <summary>
    /// One continuous active interval per tracking segment (owner rules 2026-11-09):
    /// <list type="bullet">
    /// <item>The segment starts unclassified. The first selection (a task, or a stakeholder without a task) classifies the
    /// whole unclassified interval from its start; nothing is invented before the segment or across a pause.</item>
    /// <item>Selecting a different task ends the active one at that moment and starts the new one there; selecting the same
    /// task again just continues it (one merged interval).</item>
    /// <item>Nothing else changes the active task: dismissed, missed or note-only check-ins and elapsed time do not end it.
    /// A lock is shown per <see cref="LockedTimeHandling"/> and the active task continues after it; a check-in during a
    /// lock ends it there (the user is evidently back).</item>
    /// <item>The segment end (pause, sleep, exit, crash heartbeat) ends the active interval. After a sleep or clock change
    /// (<see cref="EstimationSegment.ContinuesActive"/>) the same task continues in the next segment; the gap is never work.</item>
    /// </list>
    /// </summary>
    private static (List<TimelinePeriod> Periods, EstimationObservation? ActiveAtEnd) EstimateSegment(
        EstimationInput input, EstimationSegment segment, EstimationObservation? carried)
    {
        var (s, e) = (segment.StartUtc, segment.EndUtc);
        var result = new List<TimelinePeriod>();
        void Emit(DateTime from, DateTime to, EstimationObservation? of)
        {
            if (to > from)
            {
                result.Add(of is null
                    ? new TimelinePeriod(from, to, PeriodKind.Unclassified, null)
                    : new TimelinePeriod(from, to, PeriodKind.Estimated, of.TaskId, false, of.TaskId is null ? of.ClientId : null));
            }
        }

        // Selections and locks inside the segment, in time order (ties: locks first, then observations by CreatedAt, then Id).
        var items = new List<(DateTime At, int Order, EstimationObservation? Obs, DateTime? LockEnd)>();
        foreach (var o in input.Observations.Where(o => o.AtUtc >= s && o.AtUtc < e && (o.TaskId is not null || o.ClientId is not null)))
        {
            items.Add((o.AtUtc, 1, o, null));
        }

        foreach (var l in input.Locks.Where(l => l.EndUtc > s && l.StartUtc < e))
        {
            var lockStart = l.StartUtc < s ? s : l.StartUtc;
            var lockEnd = l.EndUtc > e ? e : l.EndUtc;

            // Any check-in while "locked" proves the user is at the computer (e.g. a missed unlock notification).
            var presence = input.Observations.Where(o => o.AtUtc > lockStart && o.AtUtc < lockEnd).Select(o => (DateTime?)o.AtUtc).Min();
            if (presence is { } back)
            {
                lockEnd = back;
            }

            if (lockEnd - lockStart > input.LockGrace)
            {
                items.Add((lockStart, 0, null, lockEnd));
            }
        }

        items.Sort((a, b) =>
        {
            var c = a.At.CompareTo(b.At);
            if (c != 0)
            {
                return c;
            }

            c = a.Order.CompareTo(b.Order);
            if (c != 0 || a.Obs is null || b.Obs is null)
            {
                return c;
            }

            c = a.Obs.CreatedAtUtc.CompareTo(b.Obs.CreatedAtUtc);
            return c != 0 ? c : a.Obs.Id.CompareTo(b.Obs.Id);
        });

        // The active interval: what it is (null = unclassified) and where it started.
        var active = carried;
        var activeStart = s;
        foreach (var item in items)
        {
            var at = item.At < activeStart ? activeStart : item.At;
            if (item.Obs is { } obs)
            {
                if (active is not null)
                {
                    Emit(activeStart, at, active); // the previous task ends here; the same task again merges in Flatten
                    activeStart = at;
                }

                active = obs; // an unclassified interval keeps its start: the selection classifies it retroactively
            }
            else if (item.LockEnd is { } lockEnd)
            {
                Emit(activeStart, at, active);
                if (lockEnd > at)
                {
                    result.Add(new TimelinePeriod(at, lockEnd, input.LockHandling == LockedTimeHandling.NotWorking ? PeriodKind.NotTracking : PeriodKind.Locked, null));
                }

                activeStart = lockEnd > at ? lockEnd : at;
            }
        }

        Emit(activeStart, e, active);
        return (result, active);
    }

    /// <summary>Puts <paramref name="top"/> over the periods, cutting whatever it overlaps.</summary>
    private static List<TimelinePeriod> Paint(List<TimelinePeriod> periods, TimelinePeriod top)
    {
        var result = new List<TimelinePeriod>(periods.Count + 2);
        foreach (var p in periods)
        {
            if (p.EndUtc <= top.StartUtc || p.StartUtc >= top.EndUtc)
            {
                result.Add(p);
                continue;
            }

            if (p.StartUtc < top.StartUtc)
            {
                result.Add(p with { EndUtc = top.StartUtc });
            }

            if (p.EndUtc > top.EndUtc)
            {
                result.Add(p with { StartUtc = top.EndUtc });
            }
        }

        result.Add(top);
        return result;
    }

    /// <summary>Clips to the range, fills gaps with NotTracking and merges equal neighbours.</summary>
    private static List<TimelinePeriod> Flatten(List<TimelinePeriod> periods, DateTime fromUtc, DateTime toUtc)
    {
        var ordered = periods
            .Select(p => p with { StartUtc = p.StartUtc < fromUtc ? fromUtc : p.StartUtc, EndUtc = p.EndUtc > toUtc ? toUtc : p.EndUtc })
            .Where(p => p.EndUtc > p.StartUtc)
            .OrderBy(p => p.StartUtc)
            .ToList();

        var result = new List<TimelinePeriod>();
        var cursor = fromUtc;
        foreach (var p in ordered)
        {
            var start = p.StartUtc < cursor ? cursor : p.StartUtc;
            if (p.EndUtc <= start)
            {
                continue;
            }

            if (start > cursor)
            {
                Add(new TimelinePeriod(cursor, start, PeriodKind.NotTracking, null));
            }

            Add(p with { StartUtc = start });
            cursor = p.EndUtc;
        }

        if (cursor < toUtc)
        {
            Add(new TimelinePeriod(cursor, toUtc, PeriodKind.NotTracking, null));
        }

        return result;

        void Add(TimelinePeriod p)
        {
            if (result.Count > 0 && result[^1] is { } last && last.EndUtc == p.StartUtc && last.Kind == p.Kind
                && last.TaskId == p.TaskId && last.ClientId == p.ClientId && last.IsNonWork == p.IsNonWork)
            {
                result[^1] = last with { EndUtc = p.EndUtc };
            }
            else
            {
                result.Add(p);
            }
        }
    }
}
