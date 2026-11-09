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
            [(At(9, 7), At(9, 10), "Unclassified"), (At(9, 10), At(9, 40), "Task A"), (At(9, 40), At(11, 40), "Task B"), (At(11, 40), At(12, 0), "(no task)")],
            entries.Select(e => (e.StartUtc, e.EndUtc, e.Task!))); // tracked from start-up; nothing silently dropped

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

    private IRegistrationService Registrations => Services.GetRequiredService<IRegistrationService>();

    [Fact]
    public async Task TimeSinceStartUp_IsUnclassified_UntilAssignedToTheFirstRegisteredTask()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var admin = await Tasks.CreateAsync(new TaskInput(await ClientIdAsync(), null, null, "Administrative tasks", null, null), Ct);
        await StartEngineAsync(); // WorkTrace starts at 09:07
        await AdvanceToAsync(10, 15);

        var open = await Registrations.GetOpenIntervalAsync(Now, Ct);
        Assert.Equal((At(9, 7), At(10, 15)), (open!.StartUtc, open.EndUtc)); // accounted from start-up, not from the first task

        await CheckIns.RecordAsync(new CheckInRequest(null, admin.Id, null), Ct);
        await Registrations.AssignAsync(open.StartUtc, open.EndUtc, admin.Id, null, Ct); // the user's confirmation
        Assert.Null(await Registrations.GetOpenIntervalAsync(Now, Ct));
        await AdvanceToAsync(11, 0);
        await CheckIns.RecordAsync(new CheckInRequest(null, admin.Id, null), Ct); // same task again: one interval
        await AdvanceToAsync(11, 30);
        await Engine.StopAsync();

        var day = Day(Now);
        Assert.Equal(new Dictionary<string, int> { ["Administrative tasks"] = 143 }, await TotalsAsync(day)); // 09:07-11:30
        var work = (await Timesheets.GetDayAsync(day, Ct)).Entries.Where(e => e.Kind == RegistrationKind.Work).ToList();
        Assert.Equal((At(9, 7), At(11, 30)), (work.First().StartUtc, work.Last().EndUtc));
        Assert.All(work.Zip(work.Skip(1)), p => Assert.Equal(p.First.EndUtc, p.Second.StartUtc)); // no gaps, no overlap
    }

    [Fact]
    public async Task Pauses_AreNeverWork_EditsAndDeletes_UpdateTheTotals()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var clientId = await ClientIdAsync();
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task A", null, null), Ct);
        var b = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task B", null, null), Ct);
        await StartEngineAsync();
        await AdvanceToAsync(9, 10);
        await CheckIns.RecordAsync(new CheckInRequest(null, a.Id, null), Ct);
        await AdvanceToAsync(10, 10);
        await Engine.PauseAsync(TimeSpan.FromMinutes(30)); // e.g. "Pause 0:30" in the check-in popup
        await AdvanceToAsync(10, 50);                      // resumed by itself at 10:40
        await CheckIns.RecordAsync(new CheckInRequest(null, a.Id, null), Ct);
        await AdvanceToAsync(11, 50);
        await Engine.StopAsync();

        var day = Day(Now);
        Assert.Equal(new Dictionary<string, int> { ["Task A"] = 120 }, await TotalsAsync(day)); // the pause never counts
        var entries = (await Timesheets.GetDayAsync(day, Ct)).Entries;
        Assert.Contains(entries, e => e.Kind == RegistrationKind.Paused && e.StartUtc == At(10, 10) && e.EndUtc == At(10, 40));
        Assert.Contains(entries, e => e.Kind == RegistrationKind.Unclassified && e.StartUtc == At(10, 40) && e.EndUtc == At(10, 50));

        await Registrations.EditAsync(At(10, 50), At(11, 50), At(10, 50), At(11, 20), b.Id, null, Ct); // shorter, other task
        Assert.Equal(new Dictionary<string, int> { ["Task A"] = 60, ["Task B"] = 30 }, await TotalsAsync(day));

        await Registrations.DeleteAsync(At(9, 10), At(10, 10), Ct);
        Assert.Equal(new Dictionary<string, int> { ["Task B"] = 30 }, await TotalsAsync(day));
        Assert.Contains((await Timesheets.GetDayAsync(day, Ct)).Entries,
            e => e.Kind == RegistrationKind.Unclassified && e.StartUtc == At(9, 7) && e.EndUtc == At(10, 10)); // kept visible, classifiable
    }
}
