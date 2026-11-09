using System.Globalization;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Management;

namespace WorkTrace.Infrastructure.Reporting;

/// <summary>
/// Planning timeline as an Excel workbook (visual timeline, task data, working days) or a landscape PDF. Both read the
/// same <see cref="PipelinePlan"/> and the same <see cref="TimelineLayout"/> facts; task data is only read.
/// </summary>
internal sealed class TimelineExporter(ILogger<TimelineExporter> logger) : ITimelineExporter
{
    // Refined Neutral tokens.
    private const string Ink = "2D2922";
    private const string Muted = "6B6355";
    private const string Accent = "5F6B45";
    private const string Band = "F2ECE1";
    private const string Rule = "D9D0C0";
    private const string WeekRule = "B8AD99";
    private const string Overdue = "A8463A";
    private const string Provisional = "C0504D";
    private const string PublicHoliday = "F2DDD9";
    private const string CustomaryHoliday = "F7EFE2";

    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    public async Task ExportExcelAsync(TimelineExportRequest request, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await StyledXlsx.WriteAsync(path, [TimelineSheet(request), TaskDataSheet(request), WorkingDaysSheet(request)], cancellationToken);
        logger.LogInformation("Timeline exported to Excel ({Tasks} tasks, {Days} days)", request.Plan.Entries.Count, request.Plan.Days.Count);
    }

    public Task ExportPdfAsync(TimelineExportRequest request, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pdf = Pdf(request);
        pdf.Save(path);
        logger.LogInformation("Timeline exported to PDF ({Tasks} tasks, {Pages} pages)", request.Plan.Entries.Count, pdf.PageCount);
        return Task.CompletedTask;
    }

    internal static string Summary(TimelineExportRequest request)
    {
        var entries = request.Plan.Entries;
        var provisional = entries.Count(e => e.IsProvisional);
        return $"{request.FilterName} · {entries.Count} task{(entries.Count == 1 ? "" : "s")} · about {TaskPipeline.FormatDuration(entries.Sum(e => e.DurationMinutes))}"
            + (provisional > 0 ? $" · {provisional} without an estimate (planned as 4 h each)" : "")
            + $" · Mon–Fri, 7.5 h per working day · generated {request.GeneratedAtLocal.ToString("d MMM yyyy HH:mm", Culture)}";
    }

    /// <summary>Index of the last shown day on or before the deadline (the marker sits at its end), or null when outside the axis.</summary>
    internal static int? DeadlineDay(TaskSummary task, IReadOnlyList<PlanDay> days)
    {
        if (task.Deadline is not { } deadline || days.Count == 0 || deadline < days[0].Date || deadline > days[^1].Date.AddDays(2))
        {
            return null;
        }

        var index = -1;
        for (var i = 0; i < days.Count && days[i].Date <= deadline; i++)
        {
            index = i;
        }

        return index >= 0 ? index : null;
    }

    private static string Contrast(string rgb)
    {
        var v = Convert.ToInt32(rgb, 16);
        var luminance = ((0.299 * ((v >> 16) & 0xFF)) + (0.587 * ((v >> 8) & 0xFF)) + (0.114 * (v & 0xFF))) / 255;
        return luminance > 0.6 ? Ink : "FFFFFF";
    }

    /// <summary>The colour at <paramref name="amount"/> opacity over white.</summary>
    private static string Tint(string rgb, double amount)
    {
        var v = Convert.ToInt32(rgb, 16);
        int Mix(int c) => (int)Math.Round(255 - ((255 - c) * amount));
        return $"{Mix((v >> 16) & 0xFF):X2}{Mix((v >> 8) & 0xFF):X2}{Mix(v & 0xFF):X2}";
    }

    private static string DayLabel(DateOnly d) => d.ToString("ddd", Culture) + "\n" + d.ToString("d/M", Culture);

    private static string Dates(QueueEntry e) => e.StartDate == e.EndDate
        ? e.StartDate.ToString("ddd d MMM", Culture)
        : $"{e.StartDate.ToString("ddd d MMM", Culture)} – {e.EndDate.ToString("ddd d MMM", Culture)}";

    private static string? DeadlineText(TaskSummary t) => t.Deadline is { } d
        ? (t.DeadlineState == DeadlineState.Overdue ? "overdue since " : "due ") + d.ToString("d MMM", Culture)
        : null;

    // ---------------------------------------------------------------- Excel

    private const int FirstDayColumn = 9;
    private const int HeaderRow = 4;

    private static XSheet TimelineSheet(TimelineExportRequest request)
    {
        var (entries, days) = (request.Plan.Entries, request.Plan.Days);
        var sheet = new XSheet("Timeline") { Landscape = true, FreezeAt = (HeaderRow + 1, FirstDayColumn) };
        var title = new XStyle(Bold: true, FontSize: 15, FontName: "Georgia", FontColor: Ink);
        var muted = new XStyle(FontColor: Muted, FontSize: 9);
        var header = new XStyle(Bold: true, Fill: Band, FontColor: Ink, BorderBottom: "thin", BorderColor: Accent, Wrap: true);
        sheet.Set(1, 1, "Planning timeline", title);
        sheet.Heights[1] = 22;
        sheet.Set(2, 1, Summary(request), muted);

        string[] headers = ["#", "Stakeholder", "Task", "Effort (h)", "Estimate", "Start", "End", "Deadline"];
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Set(HeaderRow, c + 1, headers[c], header with { Horizontal = c is 0 or 3 ? "center" : null });
        }

        foreach (var (column, width) in new[] { (1, 4.5), (2, 18), (3, 38), (4, 9), (5, 14), (6, 17), (7, 17), (8, 17) })
        {
            sheet.Widths[column] = width;
        }

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var column = FirstDayColumn + i;
            sheet.Widths[column] = 6.2;
            var holiday = day.Holiday is { } h ? (h.Kind == HolidayKind.Public ? h.Name : h.Name + "?") : null;
            var week = day.Date.DayOfWeek == DayOfWeek.Monday || i == 0 ? "W" + ISOWeek.GetWeekOfYear(day.Date.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture) : null;
            var above = string.Join(" · ", new[] { week, holiday }.Where(s => s is not null));
            if (above.Length > 0)
            {
                sheet.Set(HeaderRow - 1, column, above, new XStyle(FontSize: 8, FontColor: holiday is null ? Accent : Overdue, Italic: holiday is not null));
            }

            sheet.Set(HeaderRow, column, DayLabel(day.Date), header with
            {
                Horizontal = "center",
                FontSize = 9,
                Fill = day.CapacityMinutes == 0 ? PublicHoliday : day.Holiday is not null ? CustomaryHoliday : Band,
            });
        }

        sheet.Heights[HeaderRow] = 30;
        var lastColumn = FirstDayColumn + days.Count - 1;
        sheet.AutoFilter = (HeaderRow, 1, HeaderRow + Math.Max(1, entries.Count), lastColumn);

        for (var r = 0; r < entries.Count; r++)
        {
            var entry = entries[r];
            var task = entry.Task;
            var row = HeaderRow + 1 + r;
            var color = TimelineLayout.Rgb(task.ClientColor);
            var overdue = task.DeadlineState == DeadlineState.Overdue;
            var cell = new XStyle(BorderBottom: "hair", FontColor: Ink);
            sheet.Set(row, 1, entry.Position, cell with { Fill = color, FontColor = Contrast(color), Horizontal = "center" });
            sheet.Set(row, 2, task.ClientName, cell);
            sheet.Set(row, 3, TimelineLayout.TaskLabel(task), cell with { Bold = task.Deadline is not null, FontColor = overdue ? Overdue : Ink });
            sheet.Set(row, 4, Math.Round(entry.DurationMinutes / 60.0, 2), cell with { NumberFormat = 164, Italic = entry.IsProvisional });
            sheet.Set(row, 5, entry.IsProvisional ? "Provisional 4 h" : "Estimated",
                cell with { Italic = entry.IsProvisional, FontColor = entry.IsProvisional ? Provisional : Ink });
            sheet.Set(row, 6, entry.StartDate, cell with { NumberFormat = 165 });
            sheet.Set(row, 7, entry.EndDate, cell with { NumberFormat = 165 });
            sheet.Set(row, 8, task.Deadline is { } dl ? dl : null,
                cell with { NumberFormat = 165, Bold = task.Deadline is not null, FontColor = overdue ? Overdue : Ink });

            var minutes = TimelineLayout.MinutesPerDay(entry, days);
            var deadlineDay = DeadlineDay(task, days);
            for (var i = 0; i < days.Count; i++)
            {
                var day = days[i];
                var style = new XStyle(
                    FontSize: 9,
                    Horizontal: "center",
                    NumberFormat: 164,
                    BorderLeft: day.Date.DayOfWeek == DayOfWeek.Monday ? "thin" : "hair",
                    BorderColor: day.Date.DayOfWeek == DayOfWeek.Monday ? WeekRule : Rule,
                    BorderBottom: "hair",
                    Fill: day.CapacityMinutes == 0 ? PublicHoliday : day.Holiday is not null ? CustomaryHoliday : null);
                object? value = null;
                if (minutes[i] > 0)
                {
                    value = Math.Round(minutes[i] / 60.0, 2);
                    style = entry.IsProvisional
                        ? style with { Fill = Tint(color, 0.45), Pattern = "lightUp", PatternBack = "FFFFFF", Italic = true, FontColor = Provisional }
                        : style with { Fill = color, FontColor = Contrast(color) };
                }

                if (deadlineDay == i)
                {
                    style = style with { BorderRight = "medium", RightColor = Overdue };
                    value ??= "◆";
                    if (minutes[i] == 0)
                    {
                        style = style with { FontColor = Overdue };
                    }
                }

                sheet.Set(row, FirstDayColumn + i, value, style);
            }
        }

        // Legend under the rows.
        var legend = HeaderRow + entries.Count + 2;
        var label = new XStyle(FontColor: Muted, FontSize: 9);
        sheet.Set(legend, 3, "Legend (day cells show planned hours)", new XStyle(Bold: true, FontSize: 9, FontColor: Ink));
        (object Sample, XStyle Style, string Text)[] items =
        [
            (3.5, new XStyle(Fill: Accent, FontColor: "FFFFFF", FontSize: 9, NumberFormat: 164, Horizontal: "center"), "Estimated effort (bar in the stakeholder's colour)"),
            (4, new XStyle(Fill: Tint(Accent, 0.45), Pattern: "lightUp", PatternBack: "FFFFFF", Italic: true, FontColor: Provisional, FontSize: 9, Horizontal: "center"),
                "No estimate: provisional 4 h block (planning only, never saved)"),
            ("◆", new XStyle(FontColor: Overdue, BorderRight: "medium", RightColor: Overdue, Horizontal: "center"), "Deadline (end of that day); bold title = has a deadline, red = overdue"),
            ("", new XStyle(Fill: PublicHoliday), "Danish public holiday: no work planned"),
            ("", new XStyle(Fill: CustomaryHoliday), "Customary day off (not official): planned as a working day"),
        ];
        for (var i = 0; i < items.Length; i++)
        {
            sheet.Set(legend + 1 + i, 2, items[i].Sample, items[i].Style);
            sheet.Set(legend + 1 + i, 3, items[i].Text, label);
        }

        return sheet;
    }

    private static XSheet TaskDataSheet(TimelineExportRequest request)
    {
        var sheet = new XSheet("Task Data") { FreezeAt = (2, 5), Landscape = true };
        string[] headers =
        [
            "Position", "Stakeholder", "Reference", "Task", "Description", "Status", "Estimated remaining (h)", "Planned effort (h)", "Estimate",
            "Planned start (set)", "Scheduled start", "Scheduled end", "Deadline", "Deadline status", "Overdue",
        ];
        double[] widths = [9, 20, 14, 40, 50, 18, 13, 11, 16, 17, 17, 17, 17, 14, 9];
        var header = new XStyle(Bold: true, Fill: Band, FontColor: Ink, BorderBottom: "thin", BorderColor: Accent, Wrap: true);
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Set(1, c + 1, headers[c], header);
            sheet.Widths[c + 1] = widths[c];
        }

        sheet.Heights[1] = 30;
        var entries = request.Plan.Entries;
        sheet.AutoFilter = (1, 1, Math.Max(2, entries.Count + 1), headers.Length);
        for (var r = 0; r < entries.Count; r++)
        {
            var (entry, task, row) = (entries[r], entries[r].Task, r + 2);
            var color = TimelineLayout.Rgb(task.ClientColor);
            var overdue = task.DeadlineState == DeadlineState.Overdue;
            var cell = new XStyle(BorderBottom: "hair", FontColor: Ink);
            var date = cell with { NumberFormat = 165 };
            sheet.Set(row, 1, entry.Position, cell with { Horizontal = "center" });
            sheet.Set(row, 2, task.ClientName, cell with { BorderLeft = "thick", BorderColor = color });
            sheet.Set(row, 3, task.ExternalId, cell);
            sheet.Set(row, 4, task.Title, cell with { Bold = task.Deadline is not null, FontColor = overdue ? Overdue : Ink });
            sheet.Set(row, 5, task.Notes?.ReplaceLineEndings(" "), cell);
            sheet.Set(row, 6, TimelineLayout.Status(task), cell);
            sheet.Set(row, 7, task.EstimatedRemainingMinutes is { } m ? Math.Round(m / 60.0, 2) : null, cell with { NumberFormat = 164 });
            sheet.Set(row, 8, Math.Round(entry.DurationMinutes / 60.0, 2), cell with { NumberFormat = 164, Italic = entry.IsProvisional });
            sheet.Set(row, 9, entry.IsProvisional ? "Provisional 4 h" : "Estimated", cell with { Italic = entry.IsProvisional, FontColor = entry.IsProvisional ? Provisional : Ink });
            sheet.Set(row, 10, task.PlannedStart is { } p ? p : null, date);
            sheet.Set(row, 11, entry.StartDate, date);
            sheet.Set(row, 12, entry.EndDate, date);
            sheet.Set(row, 13, task.Deadline is { } d ? d : null, date with { Bold = task.Deadline is not null, FontColor = overdue ? Overdue : Ink });
            sheet.Set(row, 14, TimelineLayout.DeadlineStatus(task), cell with { FontColor = overdue ? Overdue : Ink, Bold = overdue });
            sheet.Set(row, 15, overdue ? "Yes" : "No", cell with { FontColor = overdue ? Overdue : Ink, Bold = overdue });
        }

        return sheet;
    }

    private static XSheet WorkingDaysSheet(TimelineExportRequest request)
    {
        var sheet = new XSheet("Working Days") { FreezeAt = (2, 1) };
        string[] headers = ["Date", "Week", "Working hours", "Planned hours", "Holiday", "Kind"];
        double[] widths = [17, 8, 13, 13, 30, 22];
        var header = new XStyle(Bold: true, Fill: Band, FontColor: Ink, BorderBottom: "thin", BorderColor: Accent);
        for (var c = 0; c < headers.Length; c++)
        {
            sheet.Set(1, c + 1, headers[c], header);
            sheet.Widths[c + 1] = widths[c];
        }

        var days = request.Plan.Days;
        var planned = new int[days.Count];
        foreach (var entry in request.Plan.Entries)
        {
            var minutes = TimelineLayout.MinutesPerDay(entry, days);
            for (var i = 0; i < days.Count; i++)
            {
                planned[i] += minutes[i];
            }
        }

        sheet.AutoFilter = (1, 1, Math.Max(2, days.Count + 1), headers.Length);
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var fill = day.CapacityMinutes == 0 ? PublicHoliday : day.Holiday is not null ? CustomaryHoliday : null;
            var cell = new XStyle(BorderBottom: "hair", FontColor: Ink, Fill: fill);
            sheet.Set(i + 2, 1, day.Date, cell with { NumberFormat = 165 });
            sheet.Set(i + 2, 2, ISOWeek.GetWeekOfYear(day.Date.ToDateTime(TimeOnly.MinValue)), cell);
            sheet.Set(i + 2, 3, day.CapacityMinutes / 60.0, cell with { NumberFormat = 164 });
            sheet.Set(i + 2, 4, Math.Round(planned[i] / 60.0, 2), cell with { NumberFormat = 164 });
            sheet.Set(i + 2, 5, day.Holiday?.Name, cell);
            sheet.Set(i + 2, 6, day.Holiday switch
            {
                { Kind: HolidayKind.Public } => "Public holiday (no work)",
                not null => "Customary day off?",
                _ => null,
            }, cell);
        }

        return sheet;
    }

    // ---------------------------------------------------------------- PDF

    private const double PageWidth = 842; // A4 landscape
    private const double PageHeight = 595;
    private const double Margin = 28;
    private const double LabelWidth = 214;
    private const double HeaderTop = Margin + 42;
    private const double HeaderHeight = 30;
    private const double RowHeight = 21;
    private const double MinDayWidth = 17;
    private const double MaxDayWidth = 70;

    internal static SimplePdf Pdf(TimelineExportRequest request)
    {
        var (entries, days) = (request.Plan.Entries, request.Plan.Days);
        var pdf = new SimplePdf(PageWidth, PageHeight);
        var gridX = Margin + LabelWidth;
        var gridWidth = PageWidth - Margin - gridX;
        var rowsTop = HeaderTop + HeaderHeight;
        var rowsPerPage = (int)((PageHeight - Margin - 26 - rowsTop) / RowHeight);

        // Wide timelines are split into date ranges (each page repeats the date header) instead of clipping tasks.
        var chunks = Math.Max(1, (int)Math.Ceiling(days.Count * MinDayWidth / gridWidth));
        var perChunk = (int)Math.Ceiling(days.Count / (double)chunks);
        var dayWidth = Math.Min(MaxDayWidth, gridWidth / Math.Max(1, perChunk));
        var rowPages = Math.Max(1, (int)Math.Ceiling(entries.Count / (double)rowsPerPage));
        var total = rowPages * chunks;
        var pageNumber = 0;

        for (var rowPage = 0; rowPage < rowPages; rowPage++)
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var c0 = chunk * perChunk;
                var c1 = Math.Min(days.Count, c0 + perChunk);
                var rows = entries.Skip(rowPage * rowsPerPage).Take(rowsPerPage).ToList();
                pdf.NewPage();
                pageNumber++;
                PageFrame(pdf, request, days, c0, c1, gridX, dayWidth, rows.Count, chunks > 1, pageNumber, total);
                for (var r = 0; r < rows.Count; r++)
                {
                    DrawRow(pdf, rows[r], days, c0, c1, gridX, dayWidth, rowsTop + (r * RowHeight));
                }

                if (entries.Count == 0)
                {
                    pdf.Text(Margin, rowsTop + 18, "No tasks match the current filter.", 9, Muted, PdfFont.Italic);
                }
            }
        }

        return pdf;
    }

    private static void PageFrame(
        SimplePdf pdf, TimelineExportRequest request, IReadOnlyList<PlanDay> days, int c0, int c1, double gridX, double dayWidth,
        int rowCount, bool split, int page, int pages)
    {
        var right = PageWidth - Margin;
        pdf.Text(Margin, Margin + 14, "Planning timeline", 17, Ink, PdfFont.Heading);
        pdf.Text(right, Margin + 12, "WorkTrace", 8, Muted, alignRight: true);
        pdf.Text(Margin, Margin + 29, Summary(request), 7.5, Muted, maxWidth: right - Margin);
        if (split && c1 > c0)
        {
            pdf.Text(right, Margin + 38, $"{days[c0].Date.ToString("d MMM", Culture)} – {days[c1 - 1].Date.ToString("d MMM yyyy", Culture)}", 7.5, Accent, PdfFont.Bold, alignRight: true);
        }

        var gridBottom = HeaderTop + HeaderHeight + (Math.Max(rowCount, 1) * RowHeight);
        var gridRight = gridX + ((c1 - c0) * dayWidth);

        // Header band and holiday columns.
        pdf.FillRect(Margin, HeaderTop, gridRight - Margin, HeaderHeight, Band);
        for (var i = c0; i < c1; i++)
        {
            var x = gridX + ((i - c0) * dayWidth);
            if (days[i].Holiday is { } holiday)
            {
                var isPublic = holiday.Kind == HolidayKind.Public;
                pdf.FillRect(x, HeaderTop, dayWidth, gridBottom - HeaderTop, isPublic ? PublicHoliday : CustomaryHoliday);
                if (!isPublic)
                {
                    pdf.StrokeRect(x + 0.5, HeaderTop + 0.5, dayWidth - 1, gridBottom - HeaderTop - 1, "B08D57", 0.4, "3 2");
                }
            }
        }

        pdf.Line(Margin, HeaderTop + HeaderHeight, gridRight, HeaderTop + HeaderHeight, Accent, 0.8);
        pdf.Text(Margin, HeaderTop + 19, "#", 7.5, Muted, PdfFont.Bold);
        pdf.Text(Margin + 18, HeaderTop + 19, "Task · stakeholder · effort · dates", 7.5, Muted, PdfFont.Bold);

        for (var i = c0; i < c1; i++)
        {
            var day = days[i];
            var x = gridX + ((i - c0) * dayWidth);
            var monday = day.Date.DayOfWeek == DayOfWeek.Monday;
            pdf.Line(x, HeaderTop, x, gridBottom, monday ? WeekRule : Rule, monday ? 0.6 : 0.35, monday ? null : "1 1.5");
            if (monday || i == c0)
            {
                pdf.Text(x + 1.5, HeaderTop + 8, "W" + ISOWeek.GetWeekOfYear(day.Date.ToDateTime(TimeOnly.MinValue)).ToString(CultureInfo.InvariantCulture), 5.5, Accent, PdfFont.Bold);
            }

            var weekday = day.Date.ToString("ddd", Culture);
            weekday = dayWidth >= 24 ? weekday : weekday[..Math.Min(2, weekday.Length)];
            pdf.Text(x + (dayWidth / 2) - (SimplePdf.Measure(weekday, 6.5, PdfFont.Regular) / 2), HeaderTop + 18, weekday, 6.5, Ink);
            var date = day.Date.ToString("d/M", Culture);
            pdf.Text(x + (dayWidth / 2) - (SimplePdf.Measure(date, 6, PdfFont.Regular) / 2), HeaderTop + 26, date, 6, Muted);
            if (day.Holiday is { } h && dayWidth >= 30 && !monday && i != c0)
            {
                pdf.Text(x + 1.5, HeaderTop + 8, h.Name, 5, Overdue, PdfFont.Italic, dayWidth - 3);
            }
        }

        pdf.Line(gridRight, HeaderTop, gridRight, gridBottom, Rule, 0.35);

        // Footer: legend and page number.
        var y = PageHeight - Margin - 4;
        var lx = Margin;
        void Legend(Action<double> sample, string text)
        {
            sample(lx);
            pdf.Text(lx + 17, y, text, 6.5, Muted);
            lx += 17 + SimplePdf.Measure(text, 6.5, PdfFont.Regular) + 14;
        }

        Legend(x => pdf.FillRect(x, y - 6, 13, 6.5, Accent), "Estimated effort (stakeholder colour)");
        Legend(x =>
        {
            pdf.FillRect(x, y - 6, 13, 6.5, Tint(Accent, 0.25));
            pdf.StrokeRect(x, y - 6, 13, 6.5, Provisional, 0.6, "2 1.5");
        }, "No estimate: provisional 4 h (not saved)");
        Legend(x => DeadlineMarker(pdf, x + 6.5, y - 8, y + 1), "Deadline (bold title; red = overdue)");
        Legend(x => pdf.FillRect(x, y - 6, 13, 6.5, PublicHoliday), "Public holiday");
        Legend(x =>
        {
            pdf.FillRect(x, y - 6, 13, 6.5, CustomaryHoliday);
            pdf.StrokeRect(x, y - 6, 13, 6.5, "B08D57", 0.4, "3 2");
        }, "Customary day off?");
        pdf.Text(right, y, $"Page {page} of {pages}", 6.5, Muted, alignRight: true);
    }

    private static void DrawRow(SimplePdf pdf, QueueEntry entry, IReadOnlyList<PlanDay> days, int c0, int c1, double gridX, double dayWidth, double top)
    {
        var task = entry.Task;
        var color = TimelineLayout.Rgb(task.ClientColor);
        var overdue = task.DeadlineState == DeadlineState.Overdue;
        var gridRight = gridX + ((c1 - c0) * dayWidth);
        pdf.Line(Margin, top + RowHeight, gridRight, top + RowHeight, "E6DFD2", 0.35);

        // Label: position, stakeholder dot, title (bold with a deadline, red when overdue), details.
        pdf.Text(Margin, top + 9, entry.Position.ToString(CultureInfo.InvariantCulture), 7, Muted);
        pdf.FillRect(Margin + 18, top + 13, 5, 5, color);
        pdf.Text(Margin + 18, top + 9, TimelineLayout.TaskLabel(task), 8, overdue ? Overdue : Ink,
            task.Deadline is not null ? PdfFont.Bold : PdfFont.Regular, LabelWidth - 24);
        var details = string.Join(" · ", new[] { task.ClientName, TimelineLayout.EffortText(entry), Dates(entry), DeadlineText(task) }.Where(s => s is not null));
        pdf.Text(Margin + 26, top + 18, details, 6, overdue ? Overdue : Muted, maxWidth: LabelWidth - 32);

        // Bar (clipped to this page's date range; the rest continues on the next range's page).
        var start = Math.Max(entry.StartDay, c0);
        var end = Math.Min(entry.EndDay, c1);
        if (end > start)
        {
            var x = gridX + ((start - c0) * dayWidth);
            var w = Math.Max(1.5, (end - start) * dayWidth);
            var y = top + 5;
            const double h = 11;
            if (entry.IsProvisional)
            {
                pdf.FillRect(x, y, w, h, Tint(color, 0.25));
                pdf.StrokeRect(x, y, w, h, Provisional, 0.7, "2 1.5");
            }
            else
            {
                pdf.FillRect(x, y, w, h, Tint(color, 0.85));
            }

            var label = entry.IsProvisional ? "4 h?" : TaskPipeline.FormatDuration(entry.DurationMinutes);
            if (SimplePdf.Measure(label, 6, PdfFont.Regular) < w - 4)
            {
                pdf.Text(x + 2, y + 8, label, 6, entry.IsProvisional ? Provisional : Contrast(Tint(color, 0.85)), entry.IsProvisional ? PdfFont.Italic : PdfFont.Regular);
            }

            if (entry.StartDay < c0)
            {
                pdf.Text(x + 1, y - 0.5, "‹", 6, Muted);
            }

            if (entry.EndDay > c1)
            {
                pdf.Text(x + w - 3, y - 0.5, "›", 6, Muted);
            }
        }

        if (DeadlineDay(task, days) is { } d && d >= c0 && d < c1)
        {
            DeadlineMarker(pdf, gridX + ((d + 1 - c0) * dayWidth), top + 2, top + RowHeight - 2);
        }
    }

    private static void DeadlineMarker(SimplePdf pdf, double x, double top, double bottom)
    {
        pdf.Line(x, top + 3, x, bottom, Overdue, 1.1);
        pdf.FillPolygon(Overdue, (x - 3, top), (x + 3, top), (x, top + 4));
    }
}
