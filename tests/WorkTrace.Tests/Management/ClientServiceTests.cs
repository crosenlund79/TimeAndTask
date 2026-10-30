using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.Tests.Management;

public class ClientServiceTests : ManagementTestBase
{
    [Fact]
    public async Task Create_TrimsAndPersists()
    {
        var created = await Clients.CreateAsync(new ClientInput("  Contoso A/S ", "  ERP customer ", false), Ct);

        Assert.Equal("Contoso A/S", created.Name);
        Assert.Equal("ERP customer", created.Description);
        Assert.True(created.IsActive);
        Assert.Equal(0, created.TaskCount);
        Assert.Contains(await Clients.GetClientsAsync(cancellationToken: Ct), c => c.Id == created.Id);
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("INTERNAL ")]
    public async Task DuplicateName_IsRejected_CaseInsensitive(string name)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Clients.CreateAsync(new ClientInput(name, null, true), Ct));
        Assert.NotNull(ex.Errors.For(nameof(ClientInput.Name)));
    }

    [Fact]
    public async Task DuplicateName_IsRejected_ForNonAsciiLetters()
    {
        await Clients.CreateAsync(new ClientInput("Ærø Kommune", null, false), Ct);
        await Assert.ThrowsAsync<DomainValidationException>(() => Clients.CreateAsync(new ClientInput("ærø kommune", null, false), Ct));
    }

    [Fact]
    public async Task Rename_ToOwnNameWithDifferentCase_IsAllowed()
    {
        var id = await InternalClientIdAsync();
        var renamed = await Clients.UpdateAsync(id, new ClientInput("INTERNAL", null, true), Ct);
        Assert.Equal("INTERNAL", renamed.Name);
    }

    [Fact]
    public async Task Update_TouchesModifiedAt_OnlyWhenSomethingChanged()
    {
        var created = await Clients.CreateAsync(new ClientInput("Fabrikam", null, false), Ct);
        Time.Advance(TimeSpan.FromMinutes(5));

        var unchanged = await Clients.UpdateAsync(created.Id, new ClientInput("Fabrikam", null, false), Ct);
        Assert.Equal(created.ModifiedAtUtc, unchanged.ModifiedAtUtc);

        var changed = await Clients.UpdateAsync(created.Id, new ClientInput("Fabrikam", "Retail", false), Ct);
        Assert.True(changed.ModifiedAtUtc > created.ModifiedAtUtc);
    }

    [Fact]
    public async Task Deactivate_HidesFromPicker_ButKeepsInFullList()
    {
        var created = await Clients.CreateAsync(new ClientInput("Northwind", null, false), Ct);
        await Clients.SetActiveAsync(created.Id, false, Ct);

        Assert.DoesNotContain(await Clients.GetClientsForPickerAsync(Ct), c => c.Id == created.Id);
        Assert.Contains(await Clients.GetClientsAsync(includeInactive: true, Ct), c => c.Id == created.Id && !c.IsActive);
        Assert.DoesNotContain(await Clients.GetClientsAsync(includeInactive: false, Ct), c => c.Id == created.Id);
    }

    [Fact]
    public async Task Picker_OrdersRecentlyUsedClientsFirst()
    {
        var a = await Clients.CreateAsync(new ClientInput("Alpha", null, false), Ct);
        var b = await Clients.CreateAsync(new ClientInput("Beta", null, false), Ct);
        var task = await Tasks.CreateAsync(new TaskInput(b.Id, null, null, "Work", null, null), Ct);
        await Tasks.MarkUsedAsync(task.Id, Ct);

        var picker = await Clients.GetClientsForPickerAsync(Ct);
        Assert.Equal(b.Id, picker[0].Id);
        Assert.True(picker.ToList().FindIndex(c => c.Id == a.Id) > 0);
        Assert.Equal(DateTimeKind.Utc, picker[0].LastUsedAtUtc!.Value.Kind);
    }

    [Fact]
    public async Task Delete_UnusedClient_Succeeds()
    {
        var created = await Clients.CreateAsync(new ClientInput("Typo Clinet", null, false), Ct);
        Assert.True(await Clients.CanDeleteAsync(created.Id, Ct));

        await Clients.DeleteAsync(created.Id, Ct);

        Assert.DoesNotContain(await Clients.GetClientsAsync(cancellationToken: Ct), c => c.Id == created.Id);
    }

    [Fact]
    public async Task Delete_ClientWithTasks_IsRefused_AndDataIsKept()
    {
        var created = await Clients.CreateAsync(new ClientInput("Litware", null, false), Ct);
        await Tasks.CreateAsync(new TaskInput(created.Id, null, null, "Kick-off", null, null), Ct);

        Assert.False(await Clients.CanDeleteAsync(created.Id, Ct));
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Clients.DeleteAsync(created.Id, Ct));
        Assert.Single(await Tasks.SearchAsync(new TaskQuery(ClientId: created.Id), Ct));
    }

    [Fact]
    public async Task Delete_LastActiveInternalClient_IsRefused()
    {
        await Assert.ThrowsAsync<OperationNotAllowedException>(async () => await Clients.DeleteAsync(await InternalClientIdAsync(), Ct));
    }

    [Fact]
    public async Task UnknownClient_Throws()
    {
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Clients.SetActiveAsync(999, false, Ct));
    }

    [Fact]
    public async Task Edits_AreWrittenToChangeLog_AsOneBatchPerSave()
    {
        var created = await Clients.CreateAsync(new ClientInput("Adventure Works", null, false), Ct);
        await Clients.UpdateAsync(created.Id, new ClientInput("Adventure Works Cycles", null, false), Ct);

        await using var db = await DbAsync();
        var log = await db.ChangeLog.Where(c => c.EntityType == "Client" && c.EntityId == created.Id).OrderBy(c => c.Id).ToListAsync(Ct);

        Assert.Equal(2, log.Count);
        Assert.Equal(WorkTrace.Core.Entities.ChangeKind.Insert, log[0].Kind);
        Assert.Null(log[0].BeforeJson);
        Assert.Contains("\"Adventure Works\"", log[0].AfterJson, StringComparison.Ordinal);
        Assert.Equal(WorkTrace.Core.Entities.ChangeKind.Update, log[1].Kind);
        Assert.Contains("\"Adventure Works\"", log[1].BeforeJson, StringComparison.Ordinal);
        Assert.Contains("\"Adventure Works Cycles\"", log[1].AfterJson, StringComparison.Ordinal);
        Assert.NotEqual(log[0].BatchId, log[1].BatchId);
        Assert.Equal(DateTimeKind.Utc, log[1].ChangedAtUtc.Kind);
    }

    [Fact]
    public async Task DeleteWithUnusedTasks_RemovesTasksWithoutTime_ButNeverRecordedTime()
    {
        var unused = await Clients.CreateAsync(new ClientInput("Prospect", null, false), Ct);
        await Tasks.CreateAsync(new TaskInput(unused.Id, null, null, "Draft offer", null, null), Ct);
        Assert.Equal(new StakeholderUsage(1, 0), await Clients.GetUsageAsync(unused.Id, Ct));

        await Clients.DeleteWithUnusedTasksAsync(unused.Id, Ct);
        Assert.DoesNotContain(await Clients.GetClientsAsync(cancellationToken: Ct), c => c.Id == unused.Id);

        var used = await Clients.CreateAsync(new ClientInput("Contoso", null, false), Ct);
        var task = await Tasks.CreateAsync(new TaskInput(used.Id, null, null, "Invoicing", null, null), Ct);
        await AddObservationAsync(task.Id);
        Assert.Equal(new StakeholderUsage(1, 1), await Clients.GetUsageAsync(used.Id, Ct));
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Clients.DeleteWithUnusedTasksAsync(used.Id, Ct));
        Assert.Single(await Tasks.SearchAsync(new TaskQuery(ClientId: used.Id), Ct));
    }
}
