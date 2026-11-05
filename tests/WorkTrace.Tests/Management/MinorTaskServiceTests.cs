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
