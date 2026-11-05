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

        await Timesheets.SetMinutesAsync(week.Days[wednesday], task.Id, null, 120, 90, Ct);
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

        await Timesheets.SetMinutesAsync(week.Days[wednesday], task.Id, null, null, 90, Ct);
        Assert.Equal(90, (await Timesheets.GetWeekAsync(week.Monday, Ct)).WeekTotal);
    }

    [Fact]
    public async Task StakeholderOnlyTime_IsItsOwnLine_AndCanBeCorrected()
    {
        var stakeholder = await Clients.CreateAsync(new ClientInput("Contoso", null, false), Ct);
        var start = Time.GetUtcNow().UtcDateTime;
        await using (var db = await DbAsync())
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = start, EndUtc = start.AddHours(1), LastHeartbeatUtc = start.AddHours(1) });
            await db.SaveChangesAsync(Ct);
        }

        await Services.GetRequiredService<WorkTrace.Core.Tracking.ICheckInService>()
            .RecordAsync(new WorkTrace.Core.Tracking.CheckInRequest(null, null, null, null, start, stakeholder.Id), Ct);

        var week = await Timesheets.GetWeekAsync(DateOnly.FromDateTime(start), Ct);
        var line = Assert.Single(week.Lines);
        Assert.Equal((null, stakeholder.Id, "Contoso", 60), (line.TaskId, line.ClientId, line.Stakeholder, line.TotalMinutes));

        var day = week.Days.ToList().IndexOf(DateOnly.FromDateTime(start.ToLocalTime()));
        await Timesheets.SetMinutesAsync(week.Days[day], null, stakeholder.Id, 45, 60, Ct);
        Assert.Equal(45, (await Timesheets.GetWeekAsync(week.Monday, Ct)).WeekTotal);
    }

    [Fact]
    public async Task Deadlines_WarnWithinTheConfiguredDays_AndStakeholdersGetADefaultColour()
    {
        var today = DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime.ToLocalTime());
        var clientId = await InternalClientIdAsync();
        var soon = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Soon", null, null, Deadline: today.AddDays(2)), Ct);
        var later = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Later", null, null, Deadline: today.AddDays(30)), Ct);
        var late = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Late", null, null, Deadline: today.AddDays(-1)), Ct);

        Assert.Equal(DeadlineState.Soon, soon.DeadlineState);
        Assert.Equal(DeadlineState.None, later.DeadlineState);
        Assert.Equal(DeadlineState.Overdue, late.DeadlineState);
        Assert.Equal(DeadlineState.None, (await Tasks.SetArchivedAsync(late.Id, true, Ct)).DeadlineState); // completed
        Assert.True(StakeholderColors.IsValid((await Clients.GetClientsAsync(cancellationToken: Ct))[0].Color));
    }

    [Fact]
    public async Task TaskGroups_WaitingWinsOverRecurring_AndClearingWaitingReturnsTheTask()
    {
        var clientId = await InternalClientIdAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Weekly report", null, null, IsRecurring: true), Ct);
        Assert.Equal(TaskGroup.Recurring, task.Group);

        var input = new TaskInput(clientId, null, null, "Weekly report", null, null, IsRecurring: true, IsWaiting: true, WaitingReason: " Figures from finance ");
        task = await Tasks.UpdateAsync(task.Id, input, Ct);
        Assert.Equal((TaskGroup.Waiting, "Figures from finance"), (task.Group, task.WaitingReason));

        task = await Tasks.UpdateAsync(task.Id, input with { IsWaiting = false }, Ct);
        Assert.Equal((TaskGroup.Recurring, (string?)null), (task.Group, task.WaitingReason));
    }

    [Fact]
    public async Task ClosedTasks_LeaveThePlanningQueue_AndUnknownEstimatesArePlannedProvisionally()
    {
        var clientId = await InternalClientIdAsync();
        var first = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Scheduled", null, null, EstimatedRemainingMinutes: 90, PlannedStart: new DateOnly(2026, 11, 10)), Ct);
        var unknown = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Unknown", null, null), Ct);
        var closed = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Done", null, null, IsWaiting: true, IsClosed: true), Ct);
        Assert.Equal(TaskGroup.Closed, closed.Group); // Closed wins over Waiting

        var queue = TaskPipeline.Build(await Tasks.SearchAsync(new TaskQuery(), Ct), new DateOnly(2026, 11, 2));
        Assert.Equal([unknown.Id, first.Id], queue.Select(e => e.Task.Id)); // chronological: unplanned work from today, then the planned task
        Assert.Equal((0, TaskPipeline.ProvisionalMinutes, true), (queue[0].StartMinutes, queue[0].DurationMinutes, queue[0].IsProvisional));
        Assert.Equal((90, false, new DateOnly(2026, 11, 10)), (queue[1].DurationMinutes, queue[1].IsProvisional, queue[1].StartDate));
        Assert.Null((await Tasks.GetAsync(unknown.Id, Ct)).EstimatedRemainingMinutes); // provisional time is never stored

        var reopened = await Tasks.UpdateAsync(closed.Id, new TaskInput(clientId, null, null, "Done", null, null, IsWaiting: true, IsClosed: false), Ct);
        Assert.Equal(TaskGroup.Waiting, reopened.Group);
    }

    [Fact]
    public async Task DaySummary_ACheckInSwitchesTheTask_AndLockedAndUnaccountedTimeAreSeparated()
    {
        var clientId = await InternalClientIdAsync();
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "A", null, null), Ct);
        var b = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "B", null, null), Ct);
        var start = Time.GetUtcNow().UtcDateTime;
        await using (var db = await DbAsync())
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = start, EndUtc = start.AddMinutes(120), LastHeartbeatUtc = start.AddMinutes(120) });
            db.Observations.Add(Observation.Create(start, start, ObservationOrigin.Manual, a.Id));
            db.Observations.Add(Observation.Create(start.AddMinutes(30), start.AddMinutes(30), ObservationOrigin.Manual, b.Id));
            db.LockPeriods.Add(new LockPeriod { StartUtc = start.AddMinutes(40), EndUtc = start.AddMinutes(50) });
            await db.SaveChangesAsync(Ct);
        }

        Time.Advance(TimeSpan.FromHours(3));
        var week = await Timesheets.GetWeekAsync(DateOnly.FromDateTime(start.ToLocalTime()), Ct);
        Assert.Equal([30, 10], week.Lines.OrderBy(l => l.Task).Select(l => l.TotalMinutes)); // A until B's check-in, B until the lock

        var day = await Timesheets.GetDaySummaryAsync(DateOnly.FromDateTime(start.ToLocalTime()), Ct);
        Assert.Equal(new DaySummary(120, 0, 40, 0, 0, 10, 70), day);
    }

    [Theory]
    [InlineData("1.5", 90)]
    [InlineData("1,25", 75)]
    [InlineData("2:30", 150)]
    [InlineData("45m", 45)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Hours_AreParsedInCommonFormats(string text, int? minutes) => Assert.Equal(minutes, Hours.ParseMinutes(text));

    [Fact]
    public void ExportRounding_RoundsEachEntryUpToTheQuarter_AndTotalsFollow()
    {
        Assert.Equal((0, 15, 15, 45, 75), (Hours.RoundUpToQuarter(0), Hours.RoundUpToQuarter(1), Hours.RoundUpToQuarter(15), Hours.RoundUpToQuarter(31), Hours.RoundUpToQuarter(67)));
        TimesheetCell Cell(int m) => new(m, m, false, false);
        var line = new TimesheetLine(1, "S", "", "", "T", "", [Cell(1), Cell(31), Cell(67), Cell(0), Cell(0), Cell(0), Cell(0)]);
        var rounded = new TimesheetWeek(new DateOnly(2026, 11, 2), [line], [0, 0, 0, 0, 0, 0, 0], 0).RoundedUpToQuarter();
        Assert.Equal(135, rounded.WeekTotal); // 15 + 45 + 75, not round(99)
    }
}
