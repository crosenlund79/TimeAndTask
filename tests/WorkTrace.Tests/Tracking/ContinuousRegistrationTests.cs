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

    /// <summary>All rows of the day as (start, end, label, active), for the Overview checks below.</summary>
    private async Task<List<(DateTime Start, DateTime End, string Label, bool Active)>> RowsAsync()
    {
        var day = await Timesheets.GetDayAsync(Day(Now), Ct);
        return [.. day.Entries.Select(e => (e.StartUtc, e.EndUtc, e.Task!, e.IsActive))];
    }

    [Fact]
    public async Task SleepLockAndAMissedUnlock_KeepTheActiveTask_AndNeverCountTheGap()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, b, _) = await SeedAsync();
        await StartEngineAsync();
        await AdvanceToAsync(9, 20);
        await Select(a);                                   // 09:07 onwards: A

        await AdvanceToAsync(9, 40);
        await Engine.NotifySuspendAsync();                 // laptop sleeps 09:40–10:10
        Sleep(TimeSpan.FromMinutes(30));
        await Engine.NotifyResumeAsync();
        await AdvanceToAsync(10, 30);
        Assert.Equal(a.Id, (await Registrations.GetActiveAsync(Now, Ct))!.TaskId); // still A: waking is not a task change

        await Engine.NotifySessionLockedAsync();           // locked 10:30–10:45
        await AdvanceToAsync(10, 45);
        await Engine.NotifySessionUnlockedAsync();
        await AdvanceToAsync(11, 0);

        await Engine.NotifySessionLockedAsync();           // locked 11:00, the unlock notification never arrives …
        await AdvanceToAsync(11, 20);
        await Select(b);                                   // … but choosing B at 11:20 proves the user is back
        await AdvanceToAsync(11, 30);

        Assert.Equal(
            [(At(9, 7), At(9, 40), "Task A", false), (At(10, 10), At(10, 30), "Task A", false), (At(10, 30), At(10, 45), "Screen locked", false),
             (At(10, 45), At(11, 0), "Task A", false), (At(11, 0), At(11, 20), "Screen locked", false), (At(11, 20), At(11, 30), "Task B", true)],
            await RowsAsync());
        Assert.DoesNotContain(await LocksAsync(), l => l.EndUtc is null); // the stale lock was closed at the check-in
        Assert.Equal(
            [(At(9, 7), At(9, 40), "Task A"), (At(10, 10), At(10, 30), "Task A"), (At(10, 45), At(11, 0), "Task A"), (At(11, 20), At(11, 30), "Task B")],
            await WorkAsync(Day(Now)));
    }

    [Fact]
    public async Task AFrozenProcessWithoutSleepNotification_IsNotCounted_AndTheTaskContinuesAfterwards()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, _, _) = await SeedAsync();
        await StartEngineAsync();
        await Select(a);
        await AdvanceToAsync(10, 0);
        Sleep(TimeSpan.FromMinutes(45));                   // Modern Standby: no notification, both clocks moved on, no timer ran
        await AdvanceToAsync(11, 0);

        var segments = await SegmentsAsync();
        Assert.Equal(TrackingEndReason.Sleep, segments[0].EndReason);
        Assert.True(segments[0].EndUtc <= At(10, 0), "ended where the engine last ran");
        var work = await WorkAsync(Day(Now));
        Assert.Equal(2, work.Count);
        Assert.All(work, w => Assert.Equal("Task A", w.Task));
        Assert.InRange(work[1].Start, At(10, 45), At(10, 50)); // the frozen 45 minutes are not work (detected within a heartbeat)
    }

    [Fact]
    public async Task APopupLeftOpenForHours_ThenMissed_DoesNotTouchTheActiveTask_AndRepeatedSelectionsShowOnOneRow()
    {
        await ConfigureAsync(t => { t.CheckInIntervalMinutes = 30; t.PromptOnStart = false; });
        var (a, b, _) = await SeedAsync();
        await StartEngineAsync();
        await AdvanceToAsync(9, 15);
        await Select(a);
        await AdvanceToAsync(9, 30, 5);                    // the 09:30 popup opens and stays open …
        await AdvanceToAsync(12, 0);
        Presenter.Complete(PromptOutcome.Closed);          // … until 12:00, closed without an answer
        await AdvanceToAsync(12, 10);
        await Select(a);                                   // A again: continues
        await AdvanceToAsync(12, 20);
        await Select(a);
        await AdvanceToAsync(12, 40);
        await Select(b);
        await AdvanceToAsync(13, 0);

        var day = await Timesheets.GetDayAsync(Day(Now), Ct);
        var rows = day.Entries.Where(e => e.Kind == RegistrationKind.Work).ToList();
        Assert.Equal([(At(9, 7), At(12, 40), "Task A", false), (At(12, 40), At(13, 0), "Task B", true)], rows.Select(e => (e.StartUtc, e.EndUtc, e.Task!, e.IsActive)));
        Assert.Equal([At(9, 15), At(12, 10), At(12, 20)], rows[0].Selections); // every selection of A is visible on its one row
        Assert.Equal([At(12, 40)], rows[1].Selections);
        Assert.DoesNotContain(day.Entries, e => e.Kind == RegistrationKind.Unclassified);
    }

    [Fact]
    public async Task AnActiveTaskAcrossMidnight_IsSplitBetweenTheDays_AndTotalsAgree()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, _, _) = await SeedAsync();
        await StartEngineAsync();
        await Select(a);
        var midnight = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        await AdvanceAsync(midnight - Now + TimeSpan.FromMinutes(30));
        await Engine.StopAsync();

        var before = await WorkAsync(Day(midnight.AddMinutes(-1)));
        var after = await WorkAsync(Day(midnight));
        Assert.Equal((At(9, 7), midnight), (before.Single().Start, before.Single().End));
        Assert.Equal((midnight, midnight.AddMinutes(30)), (after.Single().Start, after.Single().End));
    }

    [Fact]
    public async Task DiagnosticReport_ExplainsEveryBoundary_RedactsNames_AndNeverChangesTheDatabase()
    {
        await ConfigureAsync(t => { t.CheckInIntervalMinutes = 30; t.PromptOnStart = false; });
        var (a, b, _) = await SeedAsync();
        await StartEngineAsync();                          // 09:07
        await AdvanceToAsync(9, 20);
        await Select(a);
        await AdvanceToAsync(9, 30, 5);
        Presenter.Complete(PromptOutcome.TimedOut);        // a missed check-in
        await AdvanceToAsync(9, 50);
        await Engine.NotifySuspendAsync();                 // sleep 09:50–10:20
        Sleep(TimeSpan.FromMinutes(30));
        await Engine.NotifyResumeAsync();
        await AdvanceToAsync(10, 25);
        await Engine.PauseAsync(TimeSpan.FromMinutes(10));
        await CheckIns.RecordAsync(new CheckInRequest(null, b.Id, "private note"), Ct); // while paused: registers nothing
        await AdvanceToAsync(11, 0);
        await Engine.StopAsync();

        var database = Paths.DatabasePath;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); // the app's own connections settle (WAL checkpoint) first
        var before = await File.ReadAllBytesAsync(database, Ct);
        var path = await WorkTrace.Infrastructure.Reporting.RegistrationDiagnostics.WriteAsync(Paths, Day(Now), includeNames: false, Clock, Ct);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.Equal(before, await File.ReadAllBytesAsync(database, Ct)); // opened read-only: not a byte changed

        var report = await File.ReadAllTextAsync(path, Ct);
        Assert.DoesNotContain("Task A", report, StringComparison.Ordinal);       // names redacted …
        Assert.DoesNotContain("private note", report, StringComparison.Ordinal); // … and notes never included
        Assert.Contains($"T{a.Id}", report, StringComparison.Ordinal);
        Assert.Contains("first choice: classifies the unclassified time from 09:07:00", report, StringComparison.Ordinal);
        Assert.Contains("after Sleep at 09:50:00: the active task continues", report, StringComparison.Ordinal);
        Assert.Contains("OUTSIDE TRACKING", report, StringComparison.Ordinal);
        Assert.Contains("→ Missed", report, StringComparison.Ordinal);
        Assert.Contains("tracking ended (Shutdown)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ACTIVE", report, StringComparison.Ordinal); // WorkTrace was closed: nothing is running
        Assert.DoesNotContain("GAP between", report, StringComparison.Ordinal);
        Assert.DoesNotContain("OVERLAPPING", report, StringComparison.Ordinal);
        TestContext.Current.AddAttachment("registration-report", report);
    }

    /// <summary>
    /// Regression (owner data, 9 Nov 2026, allocation #4): editing the running row 13:19:29–13:19:36 to another task saved
    /// 13:19 → 13:19 as "end before start = next day": a 24-hour manual registration that overrode every later selection.
    /// </summary>
    [Fact]
    public async Task AManualRegistration_CannotReachIntoTheFuture_SoLaterSelectionsAlwaysWin()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, b, waiting) = await SeedAsync();
        await StartEngineAsync();
        await Select(a);
        await AdvanceToAsync(9, 19, 36);

        // What the editor produced for the running row: start 09:19, "end 09:19" read as the next day.
        await Assert.ThrowsAsync<WorkTrace.Core.Validation.DomainValidationException>(() =>
            Registrations.EditAsync(At(9, 7), At(9, 19, 36), At(9, 19), At(9, 19).AddDays(1), waiting.Id, null, Ct));
        Assert.Equal(a.Id, (await Registrations.GetActiveAsync(Now, Ct))!.TaskId);
    }

    /// <summary>Records already saved that way (like the owner's allocation #4) must not override what was chosen after they were made.</summary>
    [Fact]
    public async Task AnExistingFutureReachingRegistration_IsIgnoredAfterItWasMade_AndSelectionsTakeOver()
    {
        await ConfigureAsync(t => { t.PeriodicPromptsEnabled = false; t.PromptOnStart = false; });
        var (a, b, waiting) = await SeedAsync();
        await StartEngineAsync();
        await Select(a);
        await AdvanceToAsync(9, 20);
        await using (var db = await Services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<WorkTrace.Infrastructure.Persistence.WorkTraceDbContext>>().CreateDbContextAsync(Ct))
        {
            db.ManualAllocations.Add(new ManualAllocation
            {
                StartUtc = At(9, 19), EndUtc = At(9, 19).AddDays(1), Kind = AllocationKind.Task, TaskId = waiting.Id, CreatedAtUtc = Now, ModifiedAtUtc = Now,
            });
            await db.SaveChangesAsync(Ct);
        }

        // A → B → C(waiting) → A after the bad record was saved: every selection is the active task, and is registered.
        await AdvanceToAsync(10, 0);
        await Select(b);
        Assert.Equal(b.Id, (await Registrations.GetActiveAsync(Now, Ct))!.TaskId);
        await AdvanceToAsync(10, 30);
        await Select(waiting);
        await AdvanceToAsync(11, 0);
        await Select(a);
        Assert.Equal(a.Id, (await Registrations.GetActiveAsync(Now, Ct))!.TaskId);
        await AdvanceToAsync(11, 30);
        await Engine.StopAsync();

        Assert.Equal(
            [(At(9, 7), At(9, 19), "Task A"), (At(9, 19), At(9, 20), "Waiting task"), (At(9, 20), At(10, 0), "Task A"),
             (At(10, 0), At(10, 30), "Task B"), (At(10, 30), At(11, 0), "Waiting task"), (At(11, 0), At(11, 30), "Task A")],
            await WorkAsync(Day(Now)));
        Assert.Empty((await Timesheets.GetDayAsync(Day(Now).AddDays(1), Ct)).Entries); // nothing spills into tomorrow
    }
}
