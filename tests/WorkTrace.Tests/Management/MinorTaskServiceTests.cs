using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;

namespace WorkTrace.Tests.Management;

public class MinorTaskServiceTests : ManagementTestBase
{
    private IMinorTaskService Minor => Services.GetRequiredService<IMinorTaskService>();

    [Fact]
    public async Task Reminders_ComeWhenDue_SnoozeKeepsTheTime_AndCompletedOnesNeverRemind()
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var noReminder = await Minor.CreateAsync(new MinorTaskInput("Water the plants"), Ct);
        var due = await Minor.CreateAsync(new MinorTaskInput("Call the bank", ReminderAtUtc: now.AddMinutes(-5)), Ct);
        var later = await Minor.CreateAsync(new MinorTaskInput("Order toner", ReminderAtUtc: now.AddHours(2)), Ct);

        Assert.Equal([due.Id], (await Minor.GetDueAsync(now, Ct)).Select(m => m.Id)); // no time = no reminder; future = not yet

        await Minor.SnoozeAsync(due.Id, now.AddMinutes(30), Ct);
        Assert.Empty(await Minor.GetDueAsync(now.AddMinutes(10), Ct));
        var again = Assert.Single(await Minor.GetDueAsync(now.AddMinutes(31), Ct));
        Assert.Equal(now.AddMinutes(-5), again.ReminderAtUtc); // snoozing never changes the reminder time

        await Minor.SetCompletedAsync(due.Id, true, Ct);
        Assert.Equal([later.Id], (await Minor.GetDueAsync(now.AddDays(1), Ct)).Select(m => m.Id)); // completed: never again
        Assert.Equal([later.Id, noReminder.Id, due.Id], (await Minor.GetAsync(includeCompleted: true, Ct)).Select(m => m.Id));
        Assert.Equal(2, (await Minor.GetAsync(includeCompleted: false, Ct)).Count);

        // Minor tasks are not tasks: never offered for time registration or the Board.
        Assert.Empty(await Tasks.SearchAsync(new TaskQuery(IncludeArchived: true), Ct));
    }

    [Fact]
    public async Task RecurringReminder_DoneForNowMovesToTheNextOccurrence_MissedOnesAreNotReplayed_DisableKeepsTheSchedule()
    {
        Time.SetUtcNow(new DateTimeOffset(2030, 1, 7, 9, 10, 0, TimeSpan.Zero)); // Monday 09:10 (the fake clock's zone is UTC)
        var nudge = await Minor.CreateAsync(new MinorTaskInput("Use the standing desk", Recurrence: MinorTaskRecurrence.Default with { IntervalMinutes = 30 }), Ct);
        Assert.Equal(Utc(2030, 1, 7, 9, 30), nudge.ReminderAtUtc);         // 08:00 + k × 30 min
        Assert.Equal("Every 30 min · Mon–Fri 08:00–16:00", nudge.Recurrence!.Describe());

        Time.SetUtcNow(new DateTimeOffset(2030, 1, 7, 9, 31, 0, TimeSpan.Zero));
        Assert.Single(await Minor.GetDueAsync(Utc(2030, 1, 7, 9, 31), Ct));
        await Minor.AcknowledgeAsync(nudge.Id, Ct);                        // Done for now
        var after = (await Minor.GetAsync(false, Ct)).Single();
        Assert.False(after.IsCompleted);                                    // still active, same record
        Assert.Equal(Utc(2030, 1, 7, 10, 0), after.ReminderAtUtc);

        // Closed from 10:00 until 19:00: no late reminder in the evening; the schedule resumes on Tuesday 08:30.
        Time.SetUtcNow(new DateTimeOffset(2030, 1, 7, 19, 0, 0, TimeSpan.Zero));
        Assert.Empty(await Minor.GetDueAsync(Utc(2030, 1, 7, 19, 0), Ct));
        Assert.Equal(Utc(2030, 1, 8, 8, 30), (await Minor.GetAsync(false, Ct)).Single().ReminderAtUtc);

        // Friday 16:00 → the weekend is skipped.
        Assert.Equal(Utc(2030, 1, 14, 8, 30), (MinorTaskRecurrence.Default with { IntervalMinutes = 30 }).NextUtc(Utc(2030, 1, 11, 16, 0), TimeZoneInfo.Utc));
        Assert.Equal(Utc(2030, 1, 8, 10, 0),
            new MinorTaskRecurrence(WorkTrace.Core.Entities.MinorTaskRepeat.Daily, DailyAt: new TimeOnly(10, 0)).NextUtc(Utc(2030, 1, 7, 10, 0), TimeZoneInfo.Utc));

        await Minor.SetReminderDisabledAsync(nudge.Id, true, Ct);
        var disabled = (await Minor.GetAsync(false, Ct)).Single();
        Assert.Null(disabled.ReminderAtUtc);
        Assert.True(disabled.ReminderDisabled);
        Assert.Equal(30, disabled.Recurrence!.IntervalMinutes);           // configuration preserved
        Assert.Empty(await Minor.GetDueAsync(Utc(2030, 1, 9, 12, 0), Ct));

        var oneTime = await Minor.CreateAsync(new MinorTaskInput("Call the bank", ReminderAtUtc: Utc(2030, 1, 7, 18, 0)), Ct);
        await Minor.AcknowledgeAsync(oneTime.Id, Ct);                      // one-time Done still completes it
        Assert.True((await Minor.GetAsync(true, Ct)).Single(m => m.Id == oneTime.Id).IsCompleted);
        Assert.Empty(await Tasks.SearchAsync(new TaskQuery(IncludeArchived: true), Ct)); // never a task (Board, check-ins, time)
    }

    private static DateTime Utc(int y, int mo, int d, int h, int mi) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DeletingTheStakeholder_KeepsItsMinorTasks()
    {
        var client = await Clients.CreateAsync(new ClientInput("Acme", null, false), Ct);
        var task = await Minor.CreateAsync(new MinorTaskInput("Send invoice", ClientId: client.Id), Ct);
        Assert.Equal("Acme", task.ClientName);

        await Clients.DeleteAsync(client.Id, Ct);
        Assert.Null((await Minor.GetAsync(true, Ct)).Single().ClientId);
    }

    [Fact]
    public async Task WithTheBoardSwitchedOff_NoCardsAreCreated_AndExistingOnesStay()
    {
        var clientId = await InternalClientIdAsync();
        var store = Services.GetRequiredService<ISettingsStore>();
        await store.LoadAsync(Ct);
        var onBoard = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Before", null, null), Ct);
        Assert.True(onBoard.IsOnBoard);

        await store.UpdateAsync(s => s.Board.Enabled = false, Ct);
        var off = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "While off", null, null, ShowOnBoard: true), Ct);
        Assert.False(off.IsOnBoard);
        Assert.True((await Tasks.GetAsync(onBoard.Id, Ct)).IsOnBoard); // existing card kept

        await using var db = await DbAsync();
        Assert.Equal(1, await db.BoardCards.CountAsync(Ct));
    }
}
