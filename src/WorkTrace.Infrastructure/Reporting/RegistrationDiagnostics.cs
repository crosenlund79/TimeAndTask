using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Settings;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Reporting;

/// <summary>
/// Developer diagnostics: a chronological report of one day's raw registration facts (tracking segments, locks, prompts,
/// check-ins, manual registrations) next to the registrations WorkTrace computes from them, with the reason for every
/// boundary and anything unexpected. The database is opened read-only and nothing is written to it. Task and stakeholder
/// names are replaced by numbers unless asked for; check-in notes are never included.
/// </summary>
public static class RegistrationDiagnostics
{
    /// <summary>Writes the report to <c>{data}\Diagnostics</c> and returns its path.</summary>
    public static async Task<string> WriteAsync(AppPaths paths, DateOnly day, bool includeNames, TimeProvider time, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!File.Exists(paths.DatabasePath))
        {
            throw new FileNotFoundException("No WorkTrace database in the data folder.", paths.DatabasePath);
        }

        var options = new DbContextOptionsBuilder<WorkTraceDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString())
            .Options;
        await using var db = new WorkTraceDbContext(options, time);
        var report = await BuildAsync(db, ReadSettings(paths.SettingsPath), day, TimeZoneInfo.Local, UtcTime.Now(time), includeNames, cancellationToken);

        var folder = Path.Combine(paths.DataDirectory, "Diagnostics");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"registration-report-{day:yyyy-MM-dd}{(includeNames ? "-with-names" : "")}.txt");
        await File.WriteAllTextAsync(file, report, new UTF8Encoding(true), cancellationToken);
        return file;
    }

    /// <summary>Reads settings.json without ever writing it (the settings store would replace a missing or invalid file).</summary>
    private static WorkTraceSettings ReadSettings(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<WorkTraceSettings>(File.ReadAllText(path), JsonSettingsStore.SerializerOptions) ?? new WorkTraceSettings()
                : new WorkTraceSettings();
        }
        catch (JsonException)
        {
            return new WorkTraceSettings();
        }
    }

    internal static async Task<string> BuildAsync(
        WorkTraceDbContext db, WorkTraceSettings settings, DateOnly day, TimeZoneInfo zone, DateTime nowUtc, bool includeNames, CancellationToken cancellationToken)
    {
        var from = ToUtc(day, zone);
        var to = ToUtc(day.AddDays(1), zone);
        var until = nowUtc < to ? nowUtc : to;
        string L(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        static string D(TimeSpan span) => $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"WorkTrace registration report — {day:yyyy-MM-dd} ({day.DayOfWeek})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Generated {TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone):yyyy-MM-dd HH:mm:ss} local ({zone.Id}). Database opened read-only: nothing was changed.");
        sb.AppendLine(includeNames
            ? "Task and stakeholder names are INCLUDED. Check-in notes are never included."
            : "Names are replaced by numbers (T12 = task #12, S3 = stakeholder #3). Check-in notes are never included.");
        var t = settings.Tracking;
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Settings: check-ins every {t.CheckInIntervalMinutes} min ({(t.PeriodicPromptsEnabled ? "on" : "off")}), ask at start {(t.PromptOnStart ? "on" : "off")}, auto-close {t.AutoDismissSeconds} s, locked time = {t.LockedTimeHandling} (grace {t.LockGraceMinutes} min).");
        sb.AppendLine();
        if (until <= from)
        {
            sb.AppendLine("This day has not started yet.");
            return sb.ToString();
        }

        // The registrations exactly as the Overview and the Timesheet compute them.
        var (periods, _, _, _, _) = await TimesheetService.EstimateAsync(db, from, until, nowUtc, settings, cancellationToken);

        // Raw facts touching the day (from a day before, so intervals running into it are explained).
        var lookBack = from.AddDays(-1);
        var segments = await db.TrackingSegments.AsNoTracking().Where(s => s.StartUtc < until && (s.EndUtc == null || s.EndUtc > lookBack))
            .OrderBy(s => s.StartUtc).ToListAsync(cancellationToken);
        var allSegments = await db.TrackingSegments.AsNoTracking().OrderBy(s => s.StartUtc).Select(s => new { s.Id, s.StartUtc, s.EndUtc, s.EndReason })
            .ToListAsync(cancellationToken);
        var locks = await db.LockPeriods.AsNoTracking().Where(l => l.StartUtc < until && (l.EndUtc == null || l.EndUtc > lookBack))
            .OrderBy(l => l.StartUtc).ToListAsync(cancellationToken);
        var prompts = await db.CheckInPrompts.AsNoTracking().Where(p => p.ScheduledAtUtc >= from && p.ScheduledAtUtc < until)
            .OrderBy(p => p.ScheduledAtUtc).ToListAsync(cancellationToken);
        var observations = await db.Observations.AsNoTracking().Where(o => o.ObservedAtUtc >= lookBack && o.ObservedAtUtc < until)
            .OrderBy(o => o.ObservedAtUtc).ThenBy(o => o.CreatedAtUtc).ThenBy(o => o.Id).ToListAsync(cancellationToken);
        var allocations = await db.ManualAllocations.AsNoTracking().Where(a => a.StartUtc < until && a.EndUtc > from)
            .OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).ToListAsync(cancellationToken);
        var adjustments = await db.TimesheetAdjustments.AsNoTracking().Where(a => a.LocalDate == day).ToListAsync(cancellationToken);

        var taskIds = observations.Select(o => o.TaskId).Concat(allocations.Select(a => a.TaskId)).Concat(periods.Select(p => p.TaskId)).OfType<int>().Distinct().ToList();
        var tasks = await db.Tasks.AsNoTracking().Where(x => taskIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Title, x.ExternalId, x.ClientId, Stakeholder = x.Client!.Name, x.IsWaiting, x.IsClosed, x.IsArchived })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var clientIds = observations.Select(o => o.ClientId).Concat(allocations.Select(a => a.ClientId)).Concat(periods.Select(p => p.ClientId)).OfType<int>().Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        string Task(int id) => tasks.TryGetValue(id, out var x)
            ? includeNames ? $"T{id} {(string.IsNullOrWhiteSpace(x.ExternalId) ? "" : x.ExternalId + " · ")}{x.Title} [S{x.ClientId} {x.Stakeholder}]" : $"T{id} (S{x.ClientId})"
            : $"T{id} (missing)";
        string Client(int id) => includeNames && clients.TryGetValue(id, out var n) ? $"S{id} {n}" : $"S{id}";
        string Choice(int? taskId, int? clientId) => taskId is { } tid ? Task(tid) : clientId is { } cid ? $"{Client(cid)} without a task" : "no task (note only)";
        string Period(TimelinePeriod? p) => p is null ? "—" : p.Kind switch
        {
            PeriodKind.Estimated or PeriodKind.Manual => p.IsNonWork ? "break" : Choice(p.TaskId, p.ClientId),
            PeriodKind.Unclassified => "Unclassified",
            PeriodKind.Locked => "Locked",
            _ => "not tracking",
        };
        TimelinePeriod? At(DateTime utc) => periods.FirstOrDefault(p => p.StartUtc <= utc && utc < p.EndUtc);
        TimelinePeriod? Before(DateTime utc) => utc > from ? At(utc.AddTicks(-1)) : null;
        bool InSegment(DateTime utc) => segments.Any(s => s.StartUtc <= utc && (s.EndUtc ?? nowUtc) > utc);
        bool Continues(TrackingSegment s) => allSegments.LastOrDefault(x => x.StartUtc < s.StartUtc) is { } previous
            && previous.EndReason is TrackingEndReason.Sleep or TrackingEndReason.ClockChange;
        bool IsSelection(Observation o) => !o.IsDeleted && (o.TaskId is not null || o.ClientId is not null) && InSegment(o.ObservedAtUtc);

        // What a check-in did at the moment it was made (not with hindsight): the first choice in a run that started
        // unclassified classifies it from the start; the same task continues; another task switches.
        string Effect(Observation o)
        {
            var after = At(o.ObservedAtUtc);
            var segment = segments.LastOrDefault(s => s.StartUtc <= o.ObservedAtUtc);
            var firstInRun = after is not null && after.StartUtc < o.ObservedAtUtc && segment is not null
                && !observations.Any(x => x.Id != o.Id && IsSelection(x) && x.ObservedAtUtc >= after.StartUtc
                    && (x.ObservedAtUtc < o.ObservedAtUtc || x.ObservedAtUtc == o.ObservedAtUtc && x.CreatedAtUtc < o.CreatedAtUtc))
                && (after.StartUtc == segment.StartUtc && !Continues(segment) || locks.Any(l => l.EndUtc == after.StartUtc) || allocations.Any(a => a.EndUtc == after.StartUtc));
            var before = Before(o.ObservedAtUtc);
            return firstInRun ? $"first choice: classifies the unclassified time from {L(after!.StartUtc)}"
                : before is not null && before.Kind is PeriodKind.Estimated or PeriodKind.Manual && before.TaskId == o.TaskId && (o.TaskId is not null || before.ClientId == o.ClientId)
                    ? "same as active: continues"
                : before?.Kind is PeriodKind.Unclassified or PeriodKind.NotTracking or null ? $"starts {Choice(o.TaskId, o.ClientId)}"
                : $"switch: {Period(before)} → {Choice(o.TaskId, o.ClientId)}";
        }

        // ---- summary
        var daySegments = segments.Where(s => (s.EndUtc ?? nowUtc) > from).ToList();
        int Minutes(PeriodKind kind, bool work = false) => (int)Math.Round(periods.Where(p => p.Kind == kind && (!work || !p.IsNonWork)).Sum(p => p.Duration.TotalMinutes));
        sb.AppendLine("SUMMARY");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Tracking segments: {daySegments.Count} (ended by: {string.Join(", ", daySegments.GroupBy(s => s.EndUtc is null ? "still running" : s.EndReason.ToString()).Select(g => $"{g.Key} {g.Count()}"))})");
        var dayObs = observations.Where(o => o.ObservedAtUtc >= from).ToList();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Check-ins: {dayObs.Count(o => !o.IsDeleted)} (task {dayObs.Count(o => !o.IsDeleted && o.TaskId is not null)}, stakeholder only {dayObs.Count(o => !o.IsDeleted && o.TaskId is null && o.ClientId is not null)}, note only {dayObs.Count(o => !o.IsDeleted && o.TaskId is null && o.ClientId is null)}, deleted {dayObs.Count(o => o.IsDeleted)})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Check-in prompts: {prompts.Count} ({string.Join(", ", prompts.GroupBy(p => p.Outcome).Select(g => $"{g.Key} {g.Count()}"))})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Screen locks: {locks.Count(l => (l.EndUtc ?? nowUtc) > from)}; manual registrations touching the day: {allocations.Count}; timesheet corrections: {adjustments.Count}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"- Computed: work {D(TimeSpan.FromMinutes(Minutes(PeriodKind.Estimated, true) + Minutes(PeriodKind.Manual, true)))}, unclassified {D(TimeSpan.FromMinutes(Minutes(PeriodKind.Unclassified)))}, locked {D(TimeSpan.FromMinutes(Minutes(PeriodKind.Locked)))}");
        sb.AppendLine();

        // ---- chronological events
        var events = new List<(DateTime At, int Order, string Kind, string Detail)>();
        foreach (var s in segments)
        {
            var previous = allSegments.LastOrDefault(x => x.StartUtc < s.StartUtc);
            var continues = Continues(s);
            events.Add((s.StartUtc, 0, "TRACKING START", $"segment #{s.Id}; " + (previous is null ? "first ever"
                : continues ? $"after {previous.EndReason} at {L(previous.EndUtc!.Value)}: the active task continues"
                : $"after {previous.EndReason} at {(previous.EndUtc is { } pe ? L(pe) : "?")}: starts unclassified until a task is chosen")));
            events.Add((s.EndUtc ?? nowUtc, 9, s.EndUtc is null ? "TRACKING (RUNNING)" : "TRACKING END",
                s.EndUtc is null ? $"segment #{s.Id} still open; last heartbeat {L(s.LastHeartbeatUtc)}" : $"segment #{s.Id}: {s.EndReason}; last heartbeat {L(s.LastHeartbeatUtc)}"));
        }

        foreach (var l in locks)
        {
            events.Add((l.StartUtc, 1, "LOCKED", $"lock #{l.Id}"));
            events.Add((l.EndUtc ?? nowUtc, 8, l.EndUtc is null ? "LOCK (NO UNLOCK YET)" : "UNLOCKED", $"lock #{l.Id}, {D((l.EndUtc ?? nowUtc) - l.StartUtc)}"));
        }

        foreach (var p in prompts)
        {
            events.Add((p.ShownAtUtc ?? p.ScheduledAtUtc, 2, "PROMPT", $"#{p.Id} scheduled {L(p.ScheduledAtUtc)} → {p.Outcome}" + (p.ResolvedAtUtc is { } r ? $" at {L(r)}" : "")
                + (p.ObservationId is { } oid ? $" (answered by check-in #{oid})" : "") + " — a prompt never changes the active task"));
        }

        foreach (var o in observations.Where(o => o.ObservedAtUtc >= from))
        {
            var effect = o.IsDeleted ? "deleted: no effect"
                : !InSegment(o.ObservedAtUtc) ? "OUTSIDE TRACKING: no registration (paused, asleep or closed)"
                : o.TaskId is null && o.ClientId is null ? "no task chosen: active interval unchanged"
                : Effect(o);
            var timing = (o.CreatedAtUtc - o.ObservedAtUtc).Duration() > TimeSpan.FromMinutes(2) ? $"; saved at {L(o.CreatedAtUtc)} (time entered by hand or edited)" : "";
            var waiting = o.TaskId is { } tid && tasks.TryGetValue(tid, out var info) && info.IsWaiting ? "; task is Waiting for External Input" : "";
            events.Add((o.ObservedAtUtc, 3, "CHECK-IN", $"#{o.Id} {o.Origin}: {Choice(o.TaskId, o.ClientId)}{(o.Note is null ? "" : " + note")} — {effect}{timing}{waiting}"));
        }

        foreach (var a in allocations)
        {
            events.Add((a.CreatedAtUtc, 4, "MANUAL REGISTRATION", $"#{a.Id} {a.Kind}{(a.IsDeleted ? " (deleted)" : "")}: {L(a.StartUtc)}–{L(a.EndUtc)} → "
                + (a.Kind == AllocationKind.Cleared ? "unclassified" : Choice(a.TaskId, a.ClientId)) + " (overrides the estimate in that range)"));
        }

        sb.AppendLine("EVENTS (chronological; local time)");
        foreach (var e in events.Where(e => e.At >= from && e.At <= until).OrderBy(e => e.At).ThenBy(e => e.Order))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"{L(e.At)}  {e.Kind,-22} {e.Detail}");
        }

        sb.AppendLine();

        // ---- the computed registrations with the reason for each boundary
        string StartReason(TimelinePeriod p)
        {
            if (p.StartUtc == from)
            {
                return "start of day";
            }

            var reasons = new List<string>();
            if (p.Kind == PeriodKind.NotTracking)
            {
                return segments.FirstOrDefault(s => s.EndUtc == p.StartUtc) is { } ended ? $"tracking ended ({ended.EndReason})"
                    : locks.Any(l => l.StartUtc == p.StartUtc) ? "locked (locked time is not work)" : "tracking stopped";
            }

            if (segments.FirstOrDefault(s => s.StartUtc == p.StartUtc) is { } started)
            {
                reasons.Add(Continues(started) ? "tracking resumed (task continues)" : "tracking started");
            }

            if (locks.Any(l => l.EndUtc == p.StartUtc))
            {
                reasons.Add("unlocked");
            }

            if (locks.Any(l => l.StartUtc == p.StartUtc))
            {
                reasons.Add("locked");
            }

            if (observations.Any(o => IsSelection(o) && o.ObservedAtUtc == p.StartUtc))
            {
                reasons.Add("task chosen");
            }

            if (allocations.Any(a => !a.IsDeleted && (a.StartUtc == p.StartUtc || a.EndUtc == p.StartUtc)))
            {
                reasons.Add("manual registration boundary");
            }

            if (p.Kind is PeriodKind.Estimated && observations.FirstOrDefault(o => IsSelection(o) && o.ObservedAtUtc > p.StartUtc && o.ObservedAtUtc < p.EndUtc
                    && o.TaskId == p.TaskId && (p.TaskId is not null || o.ClientId == p.ClientId)) is { } first && !reasons.Contains("task chosen"))
            {
                reasons.Add($"classified retroactively by the check-in at {L(first.ObservedAtUtc)}");
            }

            return reasons.Count > 0 ? string.Join(", ", reasons) : p.Kind == PeriodKind.NotTracking ? "tracking stopped" : "continued from the previous segment";
        }

        string EndReason(TimelinePeriod p)
        {
            var reasons = new List<string>();
            if (segments.FirstOrDefault(s => s.EndUtc == p.EndUtc) is { } s)
            {
                reasons.Add($"tracking ended ({s.EndReason})");
            }
            else if (p.EndUtc == until)
            {
                return until == nowUtc ? segments.Any(x => x.EndUtc is null) && p.Kind != PeriodKind.NotTracking ? "now (ACTIVE)" : "now" : "end of day";
            }

            if (locks.Any(l => l.StartUtc == p.EndUtc))
            {
                reasons.Add("locked");
            }

            if (locks.Any(l => l.EndUtc == p.EndUtc))
            {
                reasons.Add("unlocked");
            }

            if (observations.Any(o => IsSelection(o) && o.ObservedAtUtc == p.EndUtc))
            {
                reasons.Add("another task chosen");
            }

            if (allocations.Any(a => !a.IsDeleted && (a.StartUtc == p.EndUtc || a.EndUtc == p.EndUtc)))
            {
                reasons.Add("manual registration boundary");
            }

            return reasons.Count > 0 ? string.Join(", ", reasons) : "next tracking segment";
        }

        sb.AppendLine("REGISTRATIONS (as computed for the Overview and the Timesheet)");
        sb.AppendLine("Start     End       Duration  Kind          Task                              Started because / ended because");
        foreach (var p in periods)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{L(p.StartUtc)}  {L(p.EndUtc)}  {D(p.Duration),8}  {p.Kind,-12}  {Period(p),-32}  {StartReason(p)} / {EndReason(p)}");
        }

        sb.AppendLine();

        // ---- findings
        var findings = new List<string>();
        foreach (var p in periods.Where(p => p.Kind == PeriodKind.Unclassified && p.Duration >= TimeSpan.FromMinutes(1)))
        {
            var segment = segments.LastOrDefault(s => s.StartUtc <= p.StartUtc);
            var cleared = allocations.Any(a => !a.IsDeleted && a.Kind == AllocationKind.Cleared && a.StartUtc <= p.StartUtc && a.EndUtc >= p.EndUtc);
            findings.Add($"Unclassified {L(p.StartUtc)}–{L(p.EndUtc)} ({D(p.Duration)}): " + (cleared ? "a registration was deleted here"
                : segment?.StartUtc == p.StartUtc ? "tracking (re)started here and no task was chosen before it ended"
                : "no task chosen yet in this tracking segment"));
        }

        foreach (var o in observations.Where(o => o.ObservedAtUtc >= from && !o.IsDeleted && !InSegment(o.ObservedAtUtc)))
        {
            findings.Add($"Check-in #{o.Id} at {L(o.ObservedAtUtc)} was outside tracking, so it registered nothing (tracking paused, asleep or closed then).");
        }

        foreach (var o in observations.Where(o => o.ObservedAtUtc >= from && !o.IsDeleted && locks.Any(l => l.StartUtc < o.ObservedAtUtc && (l.EndUtc ?? nowUtc) > o.ObservedAtUtc)))
        {
            findings.Add($"Check-in #{o.Id} at {L(o.ObservedAtUtc)} happened while the screen was recorded as locked (missed unlock); it now ends the lock.");
        }

        foreach (var s in daySegments.Where(s => s.EndReason is TrackingEndReason.Sleep or TrackingEndReason.ClockChange or TrackingEndReason.Crash))
        {
            var next = allSegments.FirstOrDefault(x => x.StartUtc >= s.EndUtc);
            findings.Add($"Tracking ended at {L(s.EndUtc!.Value)} ({s.EndReason}); {(next is null ? "not restarted" : $"restarted at {L(next.StartUtc)} after {D(next.StartUtc - s.EndUtc.Value)}")}.");
        }

        foreach (var (a, b) in periods.Zip(periods.Skip(1)))
        {
            if (a.EndUtc != b.StartUtc)
            {
                findings.Add($"GAP between {L(a.EndUtc)} and {L(b.StartUtc)} in the computed registrations (should never happen).");
            }
        }

        foreach (var (a, b) in segments.Zip(segments.Skip(1)))
        {
            if ((a.EndUtc ?? nowUtc) > b.StartUtc)
            {
                findings.Add($"OVERLAPPING tracking segments #{a.Id} and #{b.Id} (data integrity problem).");
            }
        }

        if (segments.Count(s => s.EndUtc is null) > 1)
        {
            findings.Add("More than one open tracking segment (data integrity problem).");
        }

        foreach (var l in locks.Where(l => l.EndUtc is null && segments.All(s => s.EndUtc is not null)))
        {
            findings.Add($"Lock #{l.Id} has no end although tracking is not running.");
        }

        foreach (var (a, b) in periods.Zip(periods.Skip(2)).Zip(periods.Skip(1), (x, middle) => (x.First, x.Second, middle))
            .Where(x => x.First.TaskId is not null && x.First.TaskId == x.Second.TaskId && x.middle.Kind == PeriodKind.NotTracking)
            .Select(x => (x.First, x.Second)))
        {
            findings.Add($"{Period(a)} is split at {L(a.EndUtc)}–{L(b.StartUtc)} by time that was not tracked (sleep, clock change or a restart).");
        }

        foreach (var a in allocations.Where(a => !a.IsDeleted && a.Kind != AllocationKind.Cleared && a.EndUtc > a.CreatedAtUtc.AddMinutes(1)))
        {
            findings.Add($"Manual registration #{a.Id} ({L(a.StartUtc)}–{TimeZoneInfo.ConvertTimeFromUtc(a.EndUtc, zone):yyyy-MM-dd HH:mm}) ends {D(a.EndUtc - a.CreatedAtUtc)} after it was saved at "
                + $"{L(a.CreatedAtUtc)}: the part after saving is ignored, so later task choices count (it would otherwise override them).");
        }

        if (adjustments.Count > 0)
        {
            findings.Add($"{adjustments.Count} timesheet correction(s) on this day change the Timesheet total but not the registrations.");
        }

        sb.AppendLine("FINDINGS");
        foreach (var f in findings.Count == 0 ? ["Nothing unexpected."] : findings)
        {
            sb.AppendLine("- " + f);
        }

        return sb.ToString();
    }

    private static DateTime ToUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
