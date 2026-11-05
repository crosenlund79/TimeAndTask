using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Reporting;

internal sealed class TimesheetService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    ISettingsStore settings,
    TimeProvider timeProvider,
    ILogger<TimesheetService> logger) : ITimesheetService
{
    public const string NoTask = "No task (notes only)";

    /// <summary>Task column of time registered on a stakeholder without a task.</summary>
    public const string NoTaskForStakeholder = "(no task)";

    public async Task<TimesheetWeek> GetWeekAsync(DateOnly anyDay, CancellationToken cancellationToken = default)
    {
        var monday = TimesheetWeek.MondayOf(anyDay);
        var zone = TimeZoneInfo.Local;
        var bounds = Enumerable.Range(0, 8).Select(i => ToUtc(monday.AddDays(i), zone)).ToArray();
        var (from, to) = (bounds[0], bounds[7]);
        var now = UtcTime.Now(timeProvider);
        var s = settings.Current;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var (periods, _, _, observations, prompts) = await EstimateAsync(db, from, to, now, s, cancellationToken);

        // Sum per day and line (split at local midnight; durations from UTC differences, so DST days have 23/25 hours).
        // A line is a task, or a stakeholder without a task (Task = null), or note-only time (both null).
        var work = new Dictionary<(int Day, int? Task, int? Client), TimeSpan>();
        var unclassified = new TimeSpan[7];
        foreach (var p in periods)
        {
            for (var d = 0; d < 7; d++)
            {
                var start = p.StartUtc > bounds[d] ? p.StartUtc : bounds[d];
                var end = p.EndUtc < bounds[d + 1] ? p.EndUtc : bounds[d + 1];
                if (end <= start)
                {
                    continue;
                }

                if (p.Kind == PeriodKind.Unclassified)
                {
                    unclassified[d] += end - start;
                }
                else if (p.Kind is PeriodKind.Estimated or PeriodKind.Manual && !p.IsNonWork)
                {
                    var key = (d, p.TaskId, p.TaskId is null ? p.ClientId : null);
                    work[key] = work.GetValueOrDefault(key) + (end - start);
                }
            }
        }

        var adjustments = await db.TimesheetAdjustments.AsNoTracking()
            .Where(a => a.LocalDate >= monday && a.LocalDate <= monday.AddDays(6)).ToListAsync(cancellationToken);
        var lineKeys = work.Keys.Select(k => (k.Task, k.Client))
            .Concat(adjustments.Select(a => (Task: a.TaskId, Client: a.TaskId is null ? a.ClientId : null)))
            .Distinct().ToList();
        var taskIds = lineKeys.Select(k => k.Task).OfType<int>().ToList();
        var tasks = await db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ExternalId, Stakeholder = t.Client!.Name, System = t.TaskSystem != null ? t.TaskSystem.Name : null })
            .ToDictionaryAsync(t => t.Id, cancellationToken);
        var clientIds = lineKeys.Select(k => k.Client).OfType<int>().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);

        var lines = new List<TimesheetLine>();
        foreach (var (taskId, clientId) in lineKeys)
        {
            var cells = new List<TimesheetCell>();
            for (var d = 0; d < 7; d++)
            {
                var computed = (int)Math.Round(work.GetValueOrDefault((d, taskId, clientId)).TotalMinutes); // exact; rounding is for exports only
                var adjustment = taskId is null && clientId is null ? null
                    : adjustments.FirstOrDefault(a => a.TaskId == taskId && (taskId is not null || a.ClientId == clientId) && a.LocalDate == monday.AddDays(d));
                cells.Add(adjustment is null
                    ? new TimesheetCell(computed, computed, false, false)
                    : new TimesheetCell(computed, adjustment.AdjustedMinutes, true, adjustment.BaselineMinutes != computed));
            }

            if (cells.All(c => c.Minutes == 0 && !c.IsAdjusted))
            {
                continue;
            }

            var comments = string.Join("; ", observations
                .Where(o => o.TaskId == taskId && (taskId is not null || o.ClientId == clientId) && o.ObservedAtUtc >= from && !string.IsNullOrWhiteSpace(o.Note))
                .OrderBy(o => o.ObservedAtUtc).Select(o => o.Note!.Trim()).Distinct(StringComparer.CurrentCultureIgnoreCase));
            lines.Add(taskId is { } tid && tasks.TryGetValue(tid, out var t)
                ? new TimesheetLine(tid, t.Stakeholder, t.System ?? "", t.ExternalId ?? "", t.Title, comments, cells)
                : clientId is { } cid
                    ? new TimesheetLine(null, clients.GetValueOrDefault(cid, ""), "", "", NoTaskForStakeholder, comments, cells, cid)
                    : new TimesheetLine(null, "", "", "", NoTask, comments, cells));
        }

        var missed = prompts.Count(p => p >= from);
        return new TimesheetWeek(
            monday,
            [.. lines.OrderBy(l => l.TaskId is null && l.ClientId is null).ThenBy(l => l.Stakeholder, StringComparer.CurrentCultureIgnoreCase).ThenBy(l => l.Task, StringComparer.CurrentCultureIgnoreCase)],
            [.. unclassified.Select(u => (int)Math.Round(u.TotalMinutes))],
            missed);
    }

    /// <summary>The estimated timeline for [from, to), with the segments and lock periods it was built from.</summary>
    private static async Task<(IReadOnlyList<TimelinePeriod> Periods, List<EstimationSegment> Segments, List<EstimationSegment> Locks, List<Observation> Observations, List<DateTime> Prompts)> EstimateAsync(
        WorkTraceDbContext db, DateTime from, DateTime to, DateTime now, WorkTraceSettings s, CancellationToken cancellationToken)
    {
        // Evidence from before the range, so estimates continue across midnight: at least a day, and back to the start
        // of any tracking segment still running at `from` (its last check-in may be older than a day).
        var lookBack = from.AddDays(-1);
        var earliest = await db.TrackingSegments.AsNoTracking().Where(x => x.StartUtc < from && (x.EndUtc == null || x.EndUtc > from))
            .MinAsync(x => (DateTime?)x.StartUtc, cancellationToken);
        if (earliest is { } start && start < lookBack)
        {
            lookBack = start;
        }

        var segments = (await db.TrackingSegments.AsNoTracking().Where(x => x.StartUtc < to && (x.EndUtc == null || x.EndUtc > lookBack)).ToListAsync(cancellationToken))
            .Select(x => new EstimationSegment(x.StartUtc, x.EndUtc ?? (now < to ? now : to))).ToList();
        var locks = (await db.LockPeriods.AsNoTracking().Where(x => x.StartUtc < to && (x.EndUtc == null || x.EndUtc > lookBack)).ToListAsync(cancellationToken))
            .Select(x => new EstimationSegment(x.StartUtc, x.EndUtc ?? now)).ToList();
        var observations = await db.Observations.AsNoTracking().Where(x => !x.IsDeleted && x.ObservedAtUtc >= lookBack && x.ObservedAtUtc < to)
            .ToListAsync(cancellationToken);
        var prompts = await db.CheckInPrompts.AsNoTracking().Where(x => x.ScheduledAtUtc >= lookBack && x.ScheduledAtUtc < to
                && (x.Outcome == CheckInOutcome.Missed || x.Outcome == CheckInOutcome.Dismissed || x.Outcome == CheckInOutcome.Suppressed))
            .Select(x => x.ScheduledAtUtc).ToListAsync(cancellationToken);
        var allocations = await db.ManualAllocations.AsNoTracking().Where(x => !x.IsDeleted && x.StartUtc < to && x.EndUtc > lookBack)
            .ToListAsync(cancellationToken);

        var input = new EstimationInput(
            segments,
            locks,
            [.. observations.Select(o => new EstimationObservation(o.Id, o.ObservedAtUtc, o.CreatedAtUtc, o.TaskId, o.ClientId))],
            prompts,
            [.. allocations.Select(a => new EstimationAllocation(a.Id, a.StartUtc, a.EndUtc, a.CreatedAtUtc, a.TaskId, a.Kind == AllocationKind.NonWork))],
            // Without periodic check-ins nothing would ever end the active task before the horizon, so a task started
            // with a manual check-in runs until the next check-in, a lock or the end of tracking (the "timer" model).
            s.Tracking.PeriodicPromptsEnabled ? TimeSpan.FromMinutes(s.Tracking.MaxEstimationHorizonMinutes) : TimeSpan.MaxValue,
            s.Tracking.LockedTimeHandling,
            TimeSpan.FromMinutes(s.Tracking.LockGraceMinutes));
        var periods = TimeEstimator.Estimate(input, from, to);
        return (periods, segments, locks, observations, prompts);
    }

    public async Task<DayRegistrations> GetDayAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var zone = TimeZoneInfo.Local;
        var now = UtcTime.Now(timeProvider);
        var from = ToUtc(day, zone);
        var to = ToUtc(day.AddDays(1), zone);
        var until = now < to ? now : to;
        var summary = await GetDaySummaryAsync(day, cancellationToken);
        var week = await GetWeekAsync(day, cancellationToken);
        var index = week.Days.ToList().IndexOf(day);
        var timesheetMinutes = week.DayTotals[index];
        var corrected = week.Lines.Any(l => l.Days[index].IsAdjusted);
        if (until <= from)
        {
            return new DayRegistrations(day, [], summary, 0, timesheetMinutes, corrected);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (periods, _, _, _, _) = await EstimateAsync(db, from, until, now, settings.Current, cancellationToken);
        var registered = periods.Where(p => p.Kind is PeriodKind.Estimated or PeriodKind.Manual).ToList();

        var taskIds = registered.Select(p => p.TaskId).OfType<int>().Distinct().ToList();
        var tasks = await db.Tasks.AsNoTracking().Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Title, t.ExternalId, t.ClientId, Stakeholder = t.Client!.Name, t.Client.Color })
            .ToDictionaryAsync(t => t.Id, cancellationToken);
        var clientIds = registered.Select(p => p.ClientId).OfType<int>().Distinct().ToList();
        var clients = await db.Clients.AsNoTracking().Where(c => clientIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Color }).ToDictionaryAsync(c => c.Id, cancellationToken);

        var entries = registered.Select(p =>
        {
            if (p.TaskId is { } tid && tasks.TryGetValue(tid, out var t))
            {
                return new RegistrationEntry(p.StartUtc, p.EndUtc, p.Kind == PeriodKind.Manual, p.IsNonWork, tid, t.Title, t.ExternalId, t.ClientId,
                    t.Stakeholder, StakeholderColors.For(t.ClientId, t.Color));
            }

            var c = p.ClientId is { } cid ? clients.GetValueOrDefault(cid) : null;
            return new RegistrationEntry(p.StartUtc, p.EndUtc, p.Kind == PeriodKind.Manual, p.IsNonWork, null,
                p.IsNonWork ? "Break / not work" : c is null ? NoTask : NoTaskForStakeholder, null,
                c?.Id, c?.Name, c is null ? null : StakeholderColors.For(c.Id, c.Color));
        }).ToList();

        // Same per-line rounding as the Timesheet, so both show the same total for the day.
        var registeredMinutes = entries.Where(e => !e.IsBreak)
            .GroupBy(e => (e.TaskId, Client: e.TaskId is null ? e.ClientId : null))
            .Sum(g => (int)Math.Round(g.Sum(e => e.Duration.TotalMinutes)));
        return new DayRegistrations(day, entries, summary, registeredMinutes, timesheetMinutes, corrected);
    }

    public async Task<DaySummary> GetDaySummaryAsync(DateOnly day, CancellationToken cancellationToken = default)
    {
        var zone = TimeZoneInfo.Local;
        var now = UtcTime.Now(timeProvider);
        var from = ToUtc(day, zone);
        var to = ToUtc(day.AddDays(1), zone);
        var until = now < to ? now : to;
        if (until <= from)
        {
            return new DaySummary(0, 0, 0, 0, 0, 0, 0);
        }

        var s = settings.Current;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var (periods, segments, locks, _, _) = await EstimateAsync(db, from, until, now, s, cancellationToken);

        int Minutes(Func<TimelinePeriod, bool> where) => (int)Math.Round(periods.Where(where).Sum(p => p.Duration.TotalMinutes));

        // Locked while tracking ran (locked time is part of the tracked span, never work).
        var locked = 0d;
        foreach (var l in locks)
        {
            foreach (var seg in segments)
            {
                var a = new[] { l.StartUtc, seg.StartUtc, from }.Max();
                var b = new[] { l.EndUtc, seg.EndUtc, until }.Min();
                if (b > a)
                {
                    locked += (b - a).TotalMinutes;
                }
            }
        }

        var lockedMinutes = (int)Math.Round(locked);
        var notWorking = s.Tracking.LockedTimeHandling == LockedTimeHandling.NotWorking;
        var unclassified = Minutes(p => p.Kind == PeriodKind.Unclassified);
        return new DaySummary(
            TrackedMinutes: Minutes(p => p.Kind != PeriodKind.NotTracking) + (notWorking ? lockedMinutes : 0),
            ConfirmedTaskMinutes: Minutes(p => p.Kind == PeriodKind.Manual && !p.IsNonWork && p.TaskId is not null),
            EstimatedTaskMinutes: Minutes(p => p.Kind == PeriodKind.Estimated && p.TaskId is not null),
            OtherMinutes: Minutes(p => p.Kind is PeriodKind.Estimated or PeriodKind.Manual && !p.IsNonWork && p.TaskId is null),
            BreakMinutes: Minutes(p => p.Kind == PeriodKind.Manual && p.IsNonWork),
            LockedMinutes: lockedMinutes,
            UnaccountedMinutes: Math.Max(0, unclassified - (notWorking ? 0 : lockedMinutes)));
    }

    public async Task SetMinutesAsync(DateOnly day, int? taskId, int? clientId, int? minutes, int computedMinutes, CancellationToken cancellationToken = default)
    {
        if (taskId is null && clientId is null)
        {
            throw new ArgumentException("A correction needs a task or a stakeholder.", nameof(taskId));
        }

        clientId = taskId is null ? clientId : null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.TimesheetAdjustments.FirstOrDefaultAsync(a => a.LocalDate == day && a.TaskId == taskId && a.ClientId == clientId, cancellationToken);
        var now = UtcTime.Now(timeProvider);
        if (minutes is null || (minutes == computedMinutes && existing is null))
        {
            if (existing is not null)
            {
                db.TimesheetAdjustments.Remove(existing);
            }
        }
        else if (existing is null)
        {
            db.TimesheetAdjustments.Add(new TimesheetAdjustment
            {
                LocalDate = day, TaskId = taskId, ClientId = clientId, AdjustedMinutes = minutes.Value, BaselineMinutes = computedMinutes, CreatedAtUtc = now, ModifiedAtUtc = now,
            });
        }
        else
        {
            existing.AdjustedMinutes = minutes.Value;
            existing.BaselineMinutes = computedMinutes;
            existing.ModifiedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Timesheet correction for task {TaskId} / stakeholder {ClientId} on {Date} {State}", taskId, clientId, day, minutes is null ? "removed" : "saved");
    }

    // ---- Export ----

    private static readonly string[] LineHeaders = ["Date", "Day", "Stakeholder", "System", "Reference", "Task", "Hours", "Comments"];

    /// <summary>Rounding happens only here: "Round hours" (nearest) per entry, then the optional round-up to 15 minutes.</summary>
    private TimesheetWeek ForExport(TimesheetWeek week)
    {
        var r = settings.Current.Reporting;
        if (r.RoundingMinutes > 0)
        {
            week = week.Rounded(m => Round(m, r.RoundingMinutes));
        }

        return r.ExportRoundUpToQuarter ? week.RoundedUpToQuarter() : week;
    }

    private static IEnumerable<object[]> LineRows(TimesheetWeek week) =>
        from d in Enumerable.Range(0, 7)
        from l in week.Lines
        where l.Days[d].Minutes > 0
        select new object[]
        {
            week.Days[d].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), week.Days[d].ToString("ddd", CultureInfo.CurrentCulture),
            l.Stakeholder, l.System, l.Reference, l.Task, Hours.FromMinutes(l.Days[d].Minutes), l.Comments,
        };

    public async Task ExportCsvAsync(TimesheetWeek week, string path, CancellationToken cancellationToken = default)
    {
        // Culture's list separator and decimal mark, so Excel on this PC opens it in columns; UTF-8 with BOM for æøå.
        week = ForExport(week);
        var culture = CultureInfo.CurrentCulture;
        var separator = culture.TextInfo.ListSeparator;
        string Field(object value)
        {
            var text = value is decimal d ? d.ToString("0.00", culture) : value.ToString() ?? "";
            return text.Contains(separator, StringComparison.Ordinal) || text.Contains('"', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)
                ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
                : text;
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, LineHeaders));
        foreach (var row in LineRows(week))
        {
            sb.AppendLine(string.Join(separator, row.Select(Field)));
        }

        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(true), cancellationToken);
    }

    public async Task ExportExcelAsync(TimesheetWeek week, string path, CancellationToken cancellationToken = default)
    {
        week = ForExport(week);
        var weekRows = new List<object[]>
        {
            new object[] { "Stakeholder", "System", "Reference", "Task" }
                .Concat(week.Days.Select(d => (object)d.ToString("ddd dd-MM", CultureInfo.CurrentCulture))).Concat(["Total", "Comments"]).ToArray(),
        };
        weekRows.AddRange(week.Lines.Select(l => new object[] { l.Stakeholder, l.System, l.Reference, l.Task }
            .Concat(l.Days.Select(c => (object)Hours.FromMinutes(c.Minutes))).Concat([Hours.FromMinutes(l.TotalMinutes), l.Comments]).ToArray()));
        weekRows.Add(new object[] { "", "", "", "Total" }.Concat(week.DayTotals.Select(t => (object)Hours.FromMinutes(t)))
            .Concat([Hours.FromMinutes(week.WeekTotal), ""]).ToArray());
        weekRows.Add(new object[] { "", "", "", "Unclassified (not registered)" }.Concat(week.UnclassifiedMinutes.Select(t => (object)Hours.FromMinutes(t)))
            .Concat([Hours.FromMinutes(week.UnclassifiedMinutes.Sum()), ""]).ToArray());

        var temp = path + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            await XlsxWriter.WriteAsync(zip, [("Week", weekRows), ("Lines", [LineHeaders.Cast<object>().ToArray(), .. LineRows(week)])], cancellationToken);
        }

        File.Move(temp, path, overwrite: true);
    }

    private static int Round(int minutes, int step) =>
        step <= 0 ? minutes : (int)(Math.Round(minutes / (double)step, MidpointRounding.AwayFromZero) * step);

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

/// <summary>Minimal .xlsx writer (inline strings, numbers with two decimals, bold header row). No third-party code.</summary>
internal static class XlsxWriter
{
    public static async Task WriteAsync(ZipArchive zip, IReadOnlyList<(string Name, List<object[]> Rows)> sheets, CancellationToken cancellationToken)
    {
        async Task Add(string name, string content)
        {
            await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken);
            await entry.WriteAsync(new UTF8Encoding(false).GetBytes(content), cancellationToken);
        }

        var overrides = string.Concat(sheets.Select((_, i) =>
            $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"));
        await Add("[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
            + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" + overrides + "</Types>");
        await Add("_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        await Add("xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" "
            + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
            + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{SecurityElement.Escape(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>")) + "</sheets></workbook>");
        await Add("xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + string.Concat(sheets.Select((_, i) =>
                $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
            + $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        await Add("xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>"
            + "<xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        for (var i = 0; i < sheets.Count; i++)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            var rows = sheets[i].Rows;
            for (var r = 0; r < rows.Count; r++)
            {
                sb.Append(CultureInfo.InvariantCulture, $"<row r=\"{r + 1}\">");
                for (var c = 0; c < rows[r].Length; c++)
                {
                    var cell = $"{Column(c)}{r + 1}";
                    if (rows[r][c] is decimal number)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" s=\"2\"><v>{number.ToString(CultureInfo.InvariantCulture)}</v></c>");
                    }
                    else if (rows[r][c]?.ToString() is { Length: > 0 } text)
                    {
                        sb.Append(CultureInfo.InvariantCulture,
                            $"<c r=\"{cell}\" t=\"inlineStr\"{(r == 0 ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">{SecurityElement.Escape(text)}</t></is></c>");
                    }
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData></worksheet>");
            await Add($"xl/worksheets/sheet{i + 1}.xml", sb.ToString());
        }
    }

    private static string Column(int index)
    {
        var name = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name;
    }
}
