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
}

/// <summary>A half-open interval [Start, End). <see cref="TaskId"/> is null for non-task time (note-only check-ins, non-work allocations).</summary>
public sealed record TimelinePeriod(DateTime StartUtc, DateTime EndUtc, PeriodKind Kind, int? TaskId, bool IsNonWork = false)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

public sealed record EstimationSegment(DateTime StartUtc, DateTime EndUtc);

public sealed record EstimationObservation(int Id, DateTime AtUtc, DateTime CreatedAtUtc, int? TaskId);

public sealed record EstimationAllocation(int Id, DateTime StartUtc, DateTime EndUtc, DateTime CreatedAtUtc, int? TaskId, bool IsNonWork);

public sealed record EstimationInput(
    IReadOnlyList<EstimationSegment> Segments,
    IReadOnlyList<EstimationSegment> Locks,
    IReadOnlyList<EstimationObservation> Observations,
    IReadOnlyList<DateTime> UncertainPrompts,
    IReadOnlyList<EstimationAllocation> Allocations,
    TimeSpan Horizon,
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
        foreach (var segment in input.Segments.Where(s => s.EndUtc > s.StartUtc).OrderBy(s => s.StartUtc))
        {
            periods.AddRange(EstimateSegment(input, segment));
        }

        // Manual allocations overlay everything (latest created wins), also outside tracking.
        foreach (var allocation in input.Allocations.Where(a => a.EndUtc > a.StartUtc).OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id))
        {
            periods = Paint(periods, new TimelinePeriod(allocation.StartUtc, allocation.EndUtc, PeriodKind.Manual, allocation.TaskId, allocation.IsNonWork));
        }

        return Flatten(periods, fromUtc, toUtc);
    }

    private static List<TimelinePeriod> EstimateSegment(EstimationInput input, EstimationSegment segment)
    {
        var (s, e) = (segment.StartUtc, segment.EndUtc);
        var result = new List<TimelinePeriod>();
        void Emit(DateTime from, DateTime to, PeriodKind kind, int? taskId = null, bool nonWork = false)
        {
            if (to > from)
            {
                result.Add(new TimelinePeriod(from, to, kind, taskId, nonWork));
            }
        }

        // Evidence inside the segment, in time order (ties: observations by CreatedAt, then Id).
        var items = new List<(DateTime At, int Order, EstimationObservation? Obs, DateTime? LockEnd)>();
        foreach (var o in input.Observations.Where(o => o.AtUtc >= s && o.AtUtc < e))
        {
            items.Add((o.AtUtc, 1, o, null));
        }

        foreach (var m in input.UncertainPrompts.Where(m => m >= s && m < e))
        {
            items.Add((m, 0, null, null));
        }

        foreach (var l in input.Locks.Where(l => l.EndUtc > s && l.StartUtc < e && l.EndUtc - l.StartUtc > input.LockGrace))
        {
            items.Add((l.StartUtc < s ? s : l.StartUtc, 0, null, l.EndUtc > e ? e : l.EndUtc));
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

        var cursor = s;
        EstimationObservation? previous = null;
        foreach (var item in items)
        {
            var at = item.At < cursor ? cursor : item.At;
            if (item.Obs is { } obs)
            {
                if (previous is null)
                {
                    Emit(cursor, at, PeriodKind.Unclassified); // no backward extrapolation
                }
                else if (previous.TaskId == obs.TaskId)
                {
                    Emit(previous.AtUtc < cursor ? cursor : previous.AtUtc, at, PeriodKind.Estimated, previous.TaskId);
                }
                else
                {
                    var from = previous.AtUtc < cursor ? cursor : previous.AtUtc;
                    var mid = from + ((at - from) / 2);
                    Emit(from, mid, PeriodKind.Estimated, previous.TaskId);
                    Emit(mid, at, PeriodKind.Estimated, obs.TaskId);
                }

                previous = obs with { AtUtc = at };
                cursor = at;
            }
            else if (item.LockEnd is { } lockEnd)
            {
                if (previous is not null)
                {
                    Emit(previous.AtUtc, at, PeriodKind.Estimated, previous.TaskId);
                }
                else
                {
                    Emit(cursor, at, PeriodKind.Unclassified);
                }

                Emit(at, lockEnd, input.LockHandling == LockedTimeHandling.NotWorking ? PeriodKind.NotTracking : PeriodKind.Unclassified);
                previous = null;
                cursor = lockEnd > cursor ? lockEnd : cursor;
            }
            else if (previous is not null)
            {
                // Missed/dismissed/suppressed prompt: estimated until it, uncertain after it.
                Emit(previous.AtUtc, at, PeriodKind.Estimated, previous.TaskId);
                previous = null;
                cursor = at;
            }
        }

        if (previous is not null)
        {
            var capped = previous.AtUtc + input.Horizon;
            var end = capped < e ? capped : e;
            Emit(previous.AtUtc, end, PeriodKind.Estimated, previous.TaskId);
            Emit(end, e, PeriodKind.Unclassified);
        }
        else
        {
            Emit(cursor, e, PeriodKind.Unclassified);
        }

        return result;
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
                && last.TaskId == p.TaskId && last.IsNonWork == p.IsNonWork)
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
