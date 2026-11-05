using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Tests.Tracking;

/// <summary>
/// Registration integrity end to end on a temporary database: the real tracking engine and check-in service write,
/// the Timesheet and the Overview day view read the same saved data.
/// </summary>
public class RegistrationIntegrityTests : TrackingTestBase
{
    private ITimesheetService Timesheets => Services.GetRequiredService<ITimesheetService>();

    private ITaskService Tasks => Services.GetRequiredService<ITaskService>();

    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local));

    private async Task<int> ClientIdAsync() =>
        (await Services.GetRequiredService<IClientService>().GetClientsAsync(cancellationToken: Ct)).Single(c => c.Name == Client.DefaultInternalName).Id;

    private async Task<WorkTraceDbContext> DbAsync() =>
        await Services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);

    /// <summary>Per task title (null key: stakeholder-only), the Timesheet minutes of the day, and checks Overview = Timesheet.</summary>
    private async Task<Dictionary<string, int>> TotalsAsync(DateOnly day)
    {
        var week = await Timesheets.GetWeekAsync(day, Ct);
        var index = week.Days.ToList().IndexOf(day);
        var registrations = await Timesheets.GetDayAsync(day, Ct);
        Assert.Equal(week.DayTotals[index], registrations.RegisteredMinutes); // daily view = weekly view, same records
        Assert.Equal(week.DayTotals[index], registrations.TimesheetMinutes);
        foreach (var (a, b) in registrations.Entries.Zip(registrations.Entries.Skip(1)))
        {
            Assert.True(a.EndUtc <= b.StartUtc, "registrations never overlap");
        }

        return week.Lines.Where(l => l.Days[index].Minutes > 0).ToDictionary(l => l.Task, l => l.Days[index].Minutes);
    }

    [Fact]
    public async Task StartSwitchStop_AreRegistered_InTheTimesheetAndOverview_AndSurviveARestart()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var clientId = await ClientIdAsync();
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task A", null, null), Ct);
        var b = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task B", null, null), Ct);
        await StartEngineAsync();

        await AdvanceToAsync(9, 10);
        await CheckIns.RecordAsync(new CheckInRequest(null, a.Id, null), Ct);   // start A
        await AdvanceToAsync(9, 40);
        await CheckIns.RecordAsync(new CheckInRequest(null, b.Id, null), Ct);   // switch to B (closes A)
        await AdvanceToAsync(10, 10);
        await CheckIns.RecordAsync(new CheckInRequest(null, b.Id, null), Ct);   // same task again: no new interval
        await AdvanceToAsync(11, 40);
        await CheckIns.RecordAsync(new CheckInRequest(null, null, null, ClientId: clientId), Ct); // stakeholder only
        await AdvanceToAsync(12, 0);
        await Engine.StopAsync(); // application exit ends the active registration

        var day = Day(Now);
        var expected = new Dictionary<string, int> { ["Task A"] = 30, ["Task B"] = 120, ["(no task)"] = 20 };
        Assert.Equal(expected, await TotalsAsync(day)); // B is not cut at the 60-min horizon when prompts are off

        var entries = (await Timesheets.GetDayAsync(day, Ct)).Entries;
        Assert.Equal(
            [(At(9, 10), At(9, 40), "Task A"), (At(9, 40), At(11, 40), "Task B"), (At(11, 40), At(12, 0), "(no task)")],
            entries.Select(e => (e.StartUtc, e.EndUtc, e.Task!)));

        await AdvanceAsync(TimeSpan.FromHours(1)); // nothing runs after exit
        Assert.Equal(expected, await TotalsAsync(day));

        await ReopenAsync(); // restart: everything is read back from the saved data
        Assert.Equal(expected, await TotalsAsync(day));
    }

    [Fact]
    public async Task ManualRegistrations_Edits_AndDeletes_UpdateTheTotals()
    {
        var clientId = await ClientIdAsync();
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task A", null, null), Ct);
        var c = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task C", null, null), Ct);
        int observationId;
        await using (var db = await DbAsync())
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = At(8, 0), EndUtc = At(9, 0), LastHeartbeatUtc = At(9, 0) });
            var obs = Observation.Create(At(8, 0), At(8, 0), ObservationOrigin.Manual, a.Id);
            db.Observations.Add(obs);
            db.ManualAllocations.Add(new ManualAllocation
            {
                StartUtc = At(8, 15), EndUtc = At(8, 45), Kind = AllocationKind.Task, TaskId = c.Id, CreatedAtUtc = Now, ModifiedAtUtc = Now,
            });
            await db.SaveChangesAsync(Ct);
            observationId = obs.Id;
        }

        var day = Day(At(8, 0));
        Assert.Equal(new Dictionary<string, int> { ["Task A"] = 30, ["Task C"] = 30 }, await TotalsAsync(day)); // manual one wins its range

        await using (var db = await DbAsync())
        {
            (await db.Observations.SingleAsync(o => o.Id == observationId, Ct)).TaskId = c.Id; // edit: A was really C
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(new Dictionary<string, int> { ["Task C"] = 60 }, await TotalsAsync(day));

        await using (var db = await DbAsync())
        {
            (await db.Observations.SingleAsync(o => o.Id == observationId, Ct)).IsDeleted = true; // delete the check-in
            (await db.ManualAllocations.SingleAsync(Ct)).IsDeleted = true;
            await db.SaveChangesAsync(Ct);
        }

        Assert.Empty(await TotalsAsync(day));
    }

    [Fact]
    public async Task RegistrationsSpanningMidnight_AreSplitBetweenTheDays()
    {
        await ConfigureAsync(t => t.PeriodicPromptsEnabled = false);
        var clientId = await ClientIdAsync();
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Late task", null, null), Ct);
        var midnight = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        await using (var db = await DbAsync())
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = midnight.AddMinutes(-90), EndUtc = midnight.AddMinutes(45), LastHeartbeatUtc = midnight.AddMinutes(45) });
            db.Observations.Add(Observation.Create(midnight.AddMinutes(-90), midnight.AddMinutes(-90), ObservationOrigin.Manual, a.Id));
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(new Dictionary<string, int> { ["Late task"] = 90 }, await TotalsAsync(new DateOnly(2026, 10, 26)));
        Assert.Equal(new Dictionary<string, int> { ["Late task"] = 45 }, await TotalsAsync(new DateOnly(2026, 10, 27)));
        var after = Assert.Single((await Timesheets.GetDayAsync(new DateOnly(2026, 10, 27), Ct)).Entries);
        Assert.Equal((midnight, midnight.AddMinutes(45)), (after.StartUtc, after.EndUtc)); // only the part in that day
    }
}
