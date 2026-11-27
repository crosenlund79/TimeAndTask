using System.Globalization;
using System.Net;
using System.Text;

namespace WorkTrace.Core.Management;

/// <summary>One task's share of the coming week. <see cref="IsProvisional"/>: the default planning block, not an estimate.</summary>
public sealed record WeeklyStatusItem(string ClientName, string? Reference, string Title, int Minutes, bool IsProvisional);

/// <summary>
/// The coming week's planned work, taken from the planning timeline (<see cref="TaskPipeline"/>). <see cref="AvailableMinutes"/>:
/// Monday-Friday at 7.5 hours, Danish public holidays excluded.
/// </summary>
public sealed record WeeklyStatusReport(
    DateOnly WeekStart,
    IReadOnlyList<WeeklyStatusItem> Items,
    int AvailableMinutes,
    IReadOnlyList<Holiday> Holidays)
{
    public int PlannedMinutes => Items.Sum(i => i.Minutes);

    public int ProvisionalMinutes => Items.Where(i => i.IsProvisional).Sum(i => i.Minutes);

    /// <summary>Planned / available hours as a whole percentage; null when the week has no working hours.</summary>
    public int? WorkloadPercent => AvailableMinutes > 0
        ? (int)Math.Round(PlannedMinutes * 100.0 / AvailableMinutes, MidpointRounding.AwayFromZero)
        : null;

    /// <summary>A third or more of the planned hours are provisional blocks.</summary>
    public bool MostlyProvisional => PlannedMinutes > 0 && ProvisionalMinutes * 3 >= PlannedMinutes;

    public int IsoWeek => ISOWeek.GetWeekOfYear(WeekStart.ToDateTime(TimeOnly.MinValue));
}

/// <summary>
/// Builds the Danish "status for den kommende uge" message for the boss from the planning timeline. Reads task data only.
/// </summary>
public static class WeeklyStatus
{
    private static readonly CultureInfo Danish = CultureInfo.GetCultureInfo("da-DK");

    /// <summary>The Monday after <paramref name="today"/> (on a weekend: the coming Monday).</summary>
    public static DateOnly NextWeekStart(DateOnly today)
    {
        var days = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(days == 0 ? 7 : days);
    }

    /// <summary>
    /// Plans the active pipeline from <paramref name="today"/> exactly as the Tasks timeline does and keeps each task's minutes
    /// inside next week. Closed, archived and recurring tasks are not in the pipeline; tasks waiting for external input are
    /// left out unless their planned start is in that week.
    /// </summary>
    public static WeeklyStatusReport Build(IEnumerable<TaskSummary> tasks, DateOnly today)
    {
        var start = NextWeekStart(today);
        var end = start.AddDays(4);
        var plan = TaskPipeline.Plan(tasks, today, maxShownDays: 1);
        var items = new List<WeeklyStatusItem>();
        foreach (var entry in plan.Entries)
        {
            var task = entry.Task;
            var minutes = entry.Allocation?.Where(d => d.Date >= start && d.Date <= end).Sum(d => d.Minutes) ?? 0;
            var plannedThisWeek = task.PlannedStart is { } planned && planned >= start && planned <= end;
            if (minutes <= 0 || (task.IsWaiting && !plannedThisWeek))
            {
                continue;
            }

            items.Add(new WeeklyStatusItem(task.ClientName, Reference(task), task.Title, minutes, entry.IsProvisional));
        }

        var holidays = new List<Holiday>();
        var available = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var holiday = DanishHolidays.Get(d);
            if (holiday?.Kind == HolidayKind.Public)
            {
                holidays.Add(holiday);
            }
            else
            {
                available += TaskPipeline.WorkingDayMinutes;
            }
        }

        return new WeeklyStatusReport(start, items, available, holidays);
    }

    /// <summary>"DevOps#4711"; the external id alone without a task system; null without an external id.</summary>
    public static string? Reference(TaskSummary task) =>
        string.IsNullOrWhiteSpace(task.ExternalId) ? null
        : string.IsNullOrWhiteSpace(task.TaskSystemName) ? task.ExternalId.Trim()
        : $"{task.TaskSystemName.Trim()}#{task.ExternalId.Trim()}";

    /// <summary>Lower case as in running Danish text ("2. juledag"), except proper names ("Kristi himmelfartsdag").</summary>
    private static string HolidayName(Holiday holiday) =>
        holiday.Name.StartsWith("Kristi", StringComparison.Ordinal) ? holiday.Name : holiday.Name.ToLower(Danish);

    /// <summary>"5", "2,5", "0,8" (hours, at most one decimal, Danish comma).</summary>
    public static string Hours(int minutes) =>
        Math.Round(minutes / 60.0, 1, MidpointRounding.AwayFromZero).ToString("0.#", Danish);

    public static string ToPlainText(WeeklyStatusReport report) => Render(report, html: false);

    /// <summary>HTML fragment (paragraphs, bold stakeholders and workload) for pasting into Outlook.</summary>
    public static string ToHtml(WeeklyStatusReport report) => Render(report, html: true);

    private static string Render(WeeklyStatusReport report, bool html)
    {
        ArgumentNullException.ThrowIfNull(report);
        var paragraphs = new List<List<string>>();
        void Para(params string[] lines) => paragraphs.Add([.. lines]);
        string E(string text) => html ? WebUtility.HtmlEncode(text) : text;
        string B(string text) => html ? $"<b>{WebUtility.HtmlEncode(text)}</b>" : text;

        Para(E("Hej Bruno,"));
        var week = $"Status for den kommende uge (uge {report.IsoWeek}):";
        if (report.Holidays.Count > 0)
        {
            var names = string.Join(" og ", report.Holidays.Select(h => $"{HolidayName(h)} ({h.Date.ToString("d. MMMM", Danish)})"));
            Para(E(week), E($"Ugen har {Hours(report.AvailableMinutes)} arbejdstimer pga. {names}."));
        }
        else
        {
            Para(E(week));
        }

        if (report.Items.Count == 0)
        {
            Para(E("Jeg har ingen kendte opgaver planlagt for den kommende uge."));
        }
        else
        {
            Para(E($"Mit konkrete forventede timetal er {Hours(report.PlannedMinutes)}; det er baseret på nedenstående kendte opgaver:"));
            foreach (var client in report.Items.GroupBy(i => i.ClientName))
            {
                var lines = new List<string> { B($"Kunde \"{client.Key}\":") };
                foreach (var item in client)
                {
                    lines.Add(E(item.Reference is null ? item.Title : $"{item.Reference} {item.Title}"));
                    lines.Add(E($"Estimat: {Hours(item.Minutes)} t.{(item.IsProvisional ? " *" : string.Empty)}"));
                }

                paragraphs.Add(lines);
            }

            if (report.ProvisionalMinutes > 0)
            {
                Para(E("* Foreløbige estimater på opgaver, der endnu ikke er specificeret i detaljer."));
            }
        }

        if (report.WorkloadPercent is { } percent)
        {
            Para(E("Det betyder, at min belægning er ca. ") + B($"{percent} %") + E("."));
        }
        else
        {
            Para(E("Belægningen kan ikke beregnes, da der ikke er nogen arbejdsdage i ugen."));
        }

        if (report.MostlyProvisional)
        {
            Para(E("En del af estimaterne er foreløbige, og jeg arbejder på at få opgaverne nærmere afklaret."));
        }

        Para(E("Ha' en god weekend"));

        if (!html)
        {
            return string.Join("\r\n\r\n", paragraphs.Select(p => string.Join("\r\n", p))) + "\r\n";
        }

        var body = new StringBuilder();
        foreach (var p in paragraphs)
        {
            body.Append("<p style=\"margin:0 0 12px 0\">").Append(string.Join("<br>", p)).Append("</p>");
        }

        return $"<div style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">{body}</div>";
    }

    /// <summary>
    /// Wraps an HTML fragment in the Windows clipboard "HTML Format" header (byte offsets into the UTF-8 text), so Outlook and
    /// Word paste it with formatting.
    /// </summary>
    public static string ToClipboardHtml(string fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        const string Header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string Before = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string After = "<!--EndFragment--></body></html>";
        var headerLength = string.Format(CultureInfo.InvariantCulture, Header, 0, 0, 0, 0).Length;
        var startHtml = headerLength;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(Before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(After);
        return string.Format(CultureInfo.InvariantCulture, Header, startHtml, endHtml, startFragment, endFragment) + Before + fragment + After;
    }
}
