using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Tests.Management;

namespace WorkTrace.Tests.Reporting;

public class TimesheetServiceTests : ManagementTestBase
{
    private ITimesheetService Timesheets => Services.GetRequiredService<ITimesheetService>();

    [Fact]
    public async Task Week_SumsEstimatedTimePerTaskAndDay_AppliesCorrections_AndExports()
    {
        var task = await Tasks.CreateAsync(new TaskInput(await InternalClientIdAsync(), null, "PRJ-7", "Invoice export", null, null), Ct);
        var start = Time.GetUtcNow().UtcDateTime; // Wednesday 28 Oct 2026, 09:00 UTC
        await using (var db = await DbAsync())
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = start, EndUtc = start.AddHours(2), LastHeartbeatUtc = start.AddHours(2) });
            db.Observations.Add(Observation.Create(start, start, ObservationOrigin.Manual, task.Id, note: "export fix"));
            db.Observations.Add(Observation.Create(start.AddMinutes(30), start.AddMinutes(30), ObservationOrigin.Manual, task.Id));
            await db.SaveChangesAsync(Ct);
        }

        var week = await Timesheets.GetWeekAsync(DateOnly.FromDateTime(start), Ct);
        var line = Assert.Single(week.Lines);
        var wednesday = week.Days.ToList().IndexOf(DateOnly.FromDateTime(start.ToLocalTime()));
        Assert.Equal(new DateOnly(2026, 10, 26), week.Monday);
        Assert.Equal(90, line.Days[wednesday].Minutes); // 30 min between check-ins + 60 min trailing horizon
        Assert.Equal(30, week.UnclassifiedMinutes.Sum());
        Assert.Equal(("PRJ-7", "export fix"), (line.Reference, line.Comments));

        await Timesheets.SetMinutesAsync(week.Days[wednesday], task.Id, 120, 90, Ct);
        week = await Timesheets.GetWeekAsync(week.Monday, Ct);
        Assert.Equal((120, true), (week.Lines[0].Days[wednesday].Minutes, week.Lines[0].Days[wednesday].IsAdjusted));
        Assert.Equal(120, week.WeekTotal);

        var dir = Path.Combine(Path.GetTempPath(), "worktrace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        await Timesheets.ExportCsvAsync(week, Path.Combine(dir, "w.csv"), Ct);
        Assert.Contains("Invoice export", await File.ReadAllTextAsync(Path.Combine(dir, "w.csv"), Ct), StringComparison.Ordinal);
        await Timesheets.ExportExcelAsync(week, Path.Combine(dir, "w.xlsx"), Ct);
        using var xlsx = ZipFile.OpenRead(Path.Combine(dir, "w.xlsx"));
        Assert.NotNull(xlsx.GetEntry("xl/worksheets/sheet2.xml"));

        await Timesheets.SetMinutesAsync(week.Days[wednesday], task.Id, null, 90, Ct);
        Assert.Equal(90, (await Timesheets.GetWeekAsync(week.Monday, Ct)).WeekTotal);
    }

    [Theory]
    [InlineData("1.5", 90)]
    [InlineData("1,25", 75)]
    [InlineData("2:30", 150)]
    [InlineData("45m", 45)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Hours_AreParsedInCommonFormats(string text, int? minutes) => Assert.Equal(minutes, Hours.ParseMinutes(text));
}
