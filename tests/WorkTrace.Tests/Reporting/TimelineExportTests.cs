using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using WorkTrace.Core.Management;
using WorkTrace.Infrastructure.Reporting;

namespace WorkTrace.Tests.Reporting;

public sealed class TimelineExportTests : IDisposable
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private readonly string _dir = Directory.CreateTempSubdirectory("worktrace-timeline-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static TaskSummary Task(int id, int? minutes, string title, DateOnly? deadline = null, DeadlineState state = DeadlineState.None, string color = "#3A6EA5") => new(
        id, 1, "Contoso", true, null, null, $"REF-{id}", title, "Notes", null, null, false, DateTime.UnixEpoch, DateTime.UnixEpoch, null,
        Deadline: deadline, ClientColor: color, DeadlineState: state, EstimatedRemainingMinutes: minutes);

    private static TimelineExportRequest Request(params TaskSummary[] tasks) =>
        new(TaskPipeline.Plan(tasks, new DateOnly(2026, 12, 23), maxShownDays: 1000), "All active tasks", new DateTime(2026, 12, 22, 10, 0, 0));

    [Fact]
    public void MinutesPerDay_FollowTheWorkingCalendar_AndSumToTheEffort()
    {
        // Wed 23 Dec, Thu 24 (customary, still a working day), Fri 25 (public holiday), Mon 28, Tue 29.
        var plan = Request(Task(1, 300, "A"), Task(2, 900, "B")).Plan;
        var b = TimelineLayout.MinutesPerDay(plan.Entries[1], plan.Days);

        Assert.Equal([150, 450, 0, 300, 0], b.Take(5)); // after A (300 min) on the 23rd; none on Juledag
        Assert.Equal(900, b.Sum());
        Assert.Equal(300, TimelineLayout.MinutesPerDay(plan.Entries[0], plan.Days).Sum());
    }

    [Fact]
    public async Task Excel_HasTimelineTaskDataAndWorkingDays_InScreenOrder()
    {
        var request = Request(
            Task(1, 300, "Invoice posting", new DateOnly(2026, 12, 28)),
            Task(2, null, "No estimate yet"),
            Task(3, 120, "Late report", new DateOnly(2026, 12, 1), DeadlineState.Overdue, "#A0675E"));
        var path = Path.Combine(_dir, "timeline.xlsx");

        await new TimelineExporter(NullLogger<TimelineExporter>.Instance).ExportExcelAsync(request, path, TestContext.Current.CancellationToken);

        await using var zip = await ZipFile.OpenReadAsync(path, TestContext.Current.CancellationToken);
        var workbook = Load(zip, "xl/workbook.xml");
        Assert.Equal(["Timeline", "Task Data", "Working Days"], workbook.Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!));
        var styles = Load(zip, "xl/styles.xml");
        Assert.Contains(styles.Descendants(Main + "patternFill"), p => (string?)p.Attribute("patternType") == "lightUp"); // provisional block
        Assert.Contains(styles.Descendants(Main + "fgColor"), c => (string?)c.Attribute("rgb") == "FFA0675E");          // stakeholder colour

        var timeline = Load(zip, "xl/worksheets/sheet1.xml");
        Assert.NotNull(timeline.Descendants(Main + "autoFilter").SingleOrDefault());
        Assert.Equal("frozen", (string?)timeline.Descendants(Main + "pane").Single().Attribute("state"));
        Assert.Equal(["REF-1 · Invoice posting", "REF-2 · No estimate yet", "REF-3 · Late report"], Column(timeline, "C").Skip(1).Take(3));

        var data = Load(zip, "xl/worksheets/sheet2.xml");
        Assert.Equal(["Invoice posting", "No estimate yet", "Late report"], Column(data, "D").Skip(1));
        Assert.Equal(["Estimated", "Provisional 4 h", "Estimated"], Column(data, "I").Skip(1));
        Assert.Equal(["No", "No", "Yes"], Column(data, "O").Skip(1));
        Assert.Equal(["On track", "No deadline", "Overdue"], Column(data, "N").Skip(1));

        var days = Load(zip, "xl/worksheets/sheet3.xml");
        Assert.Contains("Juledag", Column(days, "E"));
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.Ordinal) || e.FullName.EndsWith(".rels", StringComparison.Ordinal)))
        {
            Load(zip, entry.FullName); // every part is well-formed XML
        }
    }

    [Fact]
    public async Task Pdf_IsLandscape_PaginatesRowsAndWideTimelines_WithAValidXref()
    {
        // 70 provisional tasks = 280 h ≈ 38 working days: more rows and days than one page holds.
        var request = Request([.. Enumerable.Range(1, 70).Select(i => Task(i, null, $"Task {i} – æøå"))]);
        var path = Path.Combine(_dir, "timeline.pdf");

        await new TimelineExporter(NullLogger<TimelineExporter>.Instance).ExportPdfAsync(request, path, TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var text = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 842 595]", text, StringComparison.Ordinal);

        var pages = TimelineExporter.Pdf(request).PageCount;
        Assert.True(pages >= 4, $"expected row and date-range pages, got {pages}"); // 70 rows > 1 page, ~38 days > 1 range

        // Each xref offset points at its object.
        var xref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        var lines = text[xref..].Split('\n');
        var count = int.Parse(lines[1].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 1; i < count; i++)
        {
            var offset = int.Parse(lines[2 + i][..10], System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Pdf_EmptyTimeline_StillProducesOnePage()
    {
        var path = Path.Combine(_dir, "empty.pdf");
        await new TimelineExporter(NullLogger<TimelineExporter>.Instance).ExportPdfAsync(Request(), path, TestContext.Current.CancellationToken);
        Assert.Equal(1, TimelineExporter.Pdf(Request()).PageCount);
        Assert.True(new FileInfo(path).Length > 500);
    }

    private static XDocument Load(ZipArchive zip, string name)
    {
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    /// <summary>Text or numeric values of one column, top to bottom (empty cells skipped).</summary>
    private static List<string> Column(XDocument sheet, string column) =>
        [.. sheet.Descendants(Main + "c")
            .Where(c => ((string)c.Attribute("r")!).TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9') == column)
            .Select(c => c.Descendants(Main + "t").FirstOrDefault()?.Value ?? c.Element(Main + "v")?.Value)
            .Where(v => v is not null)
            .Select(v => v!)];
}
