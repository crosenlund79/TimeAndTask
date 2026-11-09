using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Tracking;

namespace WorkTrace.Tests.Tracking;

/// <summary>
/// One continuous active registration (owner rules 2026-11-09), end to end on a temporary database with the real engine,
/// check-in service, Timesheet and Overview day view. Never touches a real WorkTrace data folder.
/// </summary>
public class ContinuousRegistrationTests : TrackingTestBase
{
    private ITimesheetService Timesheets => Services.GetRequiredService<ITimesheetService>();

    private ITaskService Tasks => Services.GetRequiredService<ITaskService>();

    private IRegistrationService Registrations => Services.GetRequiredService<IRegistrationService>();

    private static DateOnly Day(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local));

    private async Task<(TaskSummary A, TaskSummary B, TaskSummary Waiting)> SeedAsync()
    {
        var clientId = (await Services.GetRequiredService<IClientService>().GetClientsAsync(cancellationToken: Ct)).Single(c => c.Name == Client.DefaultInternalName).Id;
        var a = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task A", null, null), Ct);
        var b = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task B", null, null), Ct);
        var waiting = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Waiting task", null, null, IsWaiting: true, WaitingReason: "customer"), Ct);
        return (a, b, waiting);
    }

    private Task Select(TaskSummary task) => CheckIns.RecordAsync(new CheckInRequest(null, task.Id, null), Ct);

    /// <summary>Work entries of the day as (start, end, task), checking Overview = Timesheet and no overlaps or gaps between them.</summary>
    private async Task<List<(DateTime Start, DateTime End, string Task)>> WorkAsync(DateOnly day)
    {
        var registrations = await Timesheets.GetDayAsync(day, Ct);
        var week = await Timesheets.GetWeekAsync(day, Ct);
        Assert.Equal(week.DayTotals[week.Days.ToList().IndexOf(day)], registrations.RegisteredMinutes); // Overview = Timesheet
        foreach (var (x, y) in registrations.Entries.Zip(registrations.Entries.Skip(1)))
        {
            Assert.True(x.EndUtc <= y.StartUtc, "no overlapping registrations");
        }

        return [.. registrations.Entries.Where(e => e.Kind == RegistrationKind.Work).Select(e => (e.StartUtc, e.EndUtc, e.Task!))];
    }

    [Fact]
    public async Task OwnerExample_StartUpIsClassified_SwitchesSplit_ReselectingContinues_ExitCloses()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, b, _) = await SeedAsync();
        await StartEngineAsync();                 // 09:07 start: unclassified
        await AdvanceToAsync(10, 7);
        await Select(a);                          // classifies 09:07 onwards as A
        await AdvanceToAsync(11, 22);
        await Select(b);                          // A ends, B starts at 11:22
        await AdvanceToAsync(11, 40);
        await Select(b);                          // same task again: nothing splits
        await AdvanceToAsync(11, 58);
        await Select(a);                          // B ends, A again
        Assert.Equal(new ActiveInterval(At(11, 58), a.Id, null), await Registrations.GetActiveAsync(Now, Ct));
        await AdvanceToAsync(16, 53);
        await Engine.StopAsync();                 // exit closes the last interval
        await AdvanceToAsync(18, 0);              // nothing runs after exit

        Assert.Equal(
            [(At(9, 7), At(11, 22), "Task A"), (At(11, 22), At(11, 58), "Task B"), (At(11, 58), At(16, 53), "Task A")],
            await WorkAsync(Day(Now)));
        Assert.Null(await Registrations.GetActiveAsync(Now, Ct)); // not tracking after exit
    }

    [Fact]
    public async Task DismissedMissedAndNoteOnlyCheckIns_NeverChangeTheActiveTask_AndWaitingTasksCanBeChosen()
    {
        await ConfigureAsync(t => { t.CheckInIntervalMinutes = 30; t.PromptOnStart = false; t.AutoDismissSeconds = 60; });
        var (a, _, waiting) = await SeedAsync();
        await StartEngineAsync();
        await AdvanceToAsync(9, 20);
        await Select(waiting);                    // "Waiting for External Input" is no obstacle

        await AdvanceToAsync(9, 30, 5);           // scheduled prompt shown …
        Presenter.Complete(PromptOutcome.Dismissed); // … and dismissed
        await AdvanceToAsync(10, 0, 5);
        Presenter.Complete(PromptOutcome.TimedOut);  // … missed
        await AdvanceToAsync(10, 10);
        await CheckIns.RecordAsync(new CheckInRequest(null, null, "just a note"), Ct); // no task chosen
        Assert.Equal(waiting.Id, (await Registrations.GetActiveAsync(Now, Ct))!.TaskId);

        await AdvanceToAsync(10, 40);
        await Select(a);
        await AdvanceToAsync(11, 0);
        await Engine.StopAsync();

        Assert.Equal([(At(9, 7), At(10, 40), "Waiting task"), (At(10, 40), At(11, 0), "Task A")], await WorkAsync(Day(Now)));
        Assert.Contains(await PromptsAsync(), p => p.Outcome == CheckInOutcome.Dismissed); // still stored as history
    }

    [Fact]
    public async Task PauseResumeRestartAndCrash_NeverOverlapOrInventTime()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, b, _) = await SeedAsync();
        await StartEngineAsync();
        await Select(a);                                   // 09:07 A
        await AdvanceToAsync(9, 30);
        await Engine.PauseAsync(TimeSpan.FromMinutes(20)); // paused 09:30–09:50, resumes by itself
        await AdvanceToAsync(10, 0);
        Assert.Equal(new ActiveInterval(At(9, 50), null, null), await Registrations.GetActiveAsync(Now, Ct)); // unclassified, not A
        await Select(b);                                   // 09:50 onwards is B
        await AdvanceToAsync(10, 30);
        await Engine.StopAsync();                          // exit at 10:30

        await AdvanceToAsync(11, 0);
        await ReopenAsync();                               // start again at 11:00
        await StartEngineAsync();
        await AdvanceToAsync(11, 20);
        await Select(a);                                   // 11:00 onwards is A (not from 10:30)
        await AdvanceToAsync(11, 40);

        // Crash: abandoned without StopAsync; the next start closes the interval at the last heartbeat (~11:37).
        var crashed = Engine;
        await StartEngineAsync();
        var heartbeat = (await SegmentsAsync()).Single(s => s.EndReason == TrackingEndReason.Crash).EndUtc!.Value;
        await Engine.StopAsync();
        GC.KeepAlive(crashed);

        Assert.Equal(
            [(At(9, 7), At(9, 30), "Task A"), (At(9, 50), At(10, 30), "Task B"), (At(11, 0), heartbeat, "Task A")],
            await WorkAsync(Day(Now)));
        Assert.True(heartbeat <= At(11, 40), "no time invented after the crash");
    }

    [Fact]
    public async Task DailyTaskHistory_ListsEachTaskOnce_ByDay_IncludingBriefOnes_AndAcrossMidnight()
    {
        var (a, b, waiting) = await SeedAsync();
        var midnight = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        await using (var db = await Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<WorkTrace.Infrastructure.Persistence.WorkTraceDbContext>>()
            .CreateDbContextAsync(Ct))
        {
            db.TrackingSegments.Add(new TrackingSegment { StartUtc = midnight.AddHours(-3), EndUtc = midnight.AddHours(1), LastHeartbeatUtc = midnight.AddHours(1) });
            db.Observations.Add(Observation.Create(midnight.AddHours(-2), midnight.AddHours(-2), ObservationOrigin.Manual, a.Id));
            db.Observations.Add(Observation.Create(midnight.AddMinutes(-90), midnight.AddMinutes(-90), ObservationOrigin.Manual, waiting.Id)); // brief:
            db.Observations.Add(Observation.Create(midnight.AddMinutes(-90), midnight.AddMinutes(-89), ObservationOrigin.Manual, b.Id));       // replaced at once
            db.Observations.Add(Observation.Create(midnight.AddMinutes(-60), midnight.AddMinutes(-60), ObservationOrigin.Manual, a.Id));       // A again
            await db.SaveChangesAsync(Ct);
        }

        var before = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(midnight.AddHours(-1), TimeZoneInfo.Local));
        var after = before.AddDays(1);
        var day1 = await Timesheets.GetDayAsync(before, Ct);
        Assert.Equal(["Task A", "Waiting task", "Task B"], day1.Tasks.Select(t => t.Task)); // once each, by first use; no "Unclassified"
        Assert.All(day1.Tasks, t => Assert.Equal(Client.DefaultInternalName, t.Stakeholder));
        Assert.Equal(["Task A"], (await Timesheets.GetDayAsync(after, Ct)).Tasks.Select(t => t.Task)); // A ran past midnight

        var week = await Timesheets.GetWeekAsync(before, Ct);
        Assert.Equal(day1.Tasks, week.DayTasks[week.Days.ToList().IndexOf(before)]);
        Assert.Equal(day1.RegisteredMinutes, week.DayTotals[week.Days.ToList().IndexOf(before)]); // informational only
    }
}
