using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.Tests.Management;

public class LookupServiceTests : ManagementTestBase
{
    [Theory]
    [InlineData(LookupKind.TaskSystem)]
    [InlineData(LookupKind.CommunicationChannel)]
    public async Task Create_AppendsAtEnd(LookupKind kind)
    {
        var created = await Lookups.CreateAsync(kind, "  Jira ", Ct);

        var all = await Lookups.GetAsync(kind, cancellationToken: Ct);
        Assert.Equal("Jira", all[^1].Name);
        Assert.Equal(created.Id, all[^1].Id);
        Assert.False(created.IsBuiltIn);
    }

    [Fact]
    public async Task DuplicateNames_AreRejected_PerList()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => Lookups.CreateAsync(LookupKind.TaskSystem, "crm", Ct));

        // Same name in the other list is fine: the lists are independent.
        var channel = await Lookups.CreateAsync(LookupKind.CommunicationChannel, "CRM", Ct);
        Assert.Equal("CRM", channel.Name);
    }

    [Fact]
    public async Task BuiltIn_CanBeRenamedAndHidden_ButNotDeleted()
    {
        var phone = await LookupIdAsync(LookupKind.CommunicationChannel, "Phone");

        var renamed = await Lookups.RenameAsync(LookupKind.CommunicationChannel, phone, "Telefon", Ct);
        var hidden = await Lookups.SetActiveAsync(LookupKind.CommunicationChannel, phone, false, Ct);

        Assert.Equal("Telefon", renamed.Name);
        Assert.False(hidden.IsActive);
        Assert.DoesNotContain(await Lookups.GetAsync(LookupKind.CommunicationChannel, includeInactive: false, Ct), l => l.Id == phone);
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Lookups.DeleteAsync(LookupKind.CommunicationChannel, phone, Ct));
    }

    [Fact]
    public async Task UsedItem_CannotBeDeleted_AndReportsUsage()
    {
        var jira = await Lookups.CreateAsync(LookupKind.TaskSystem, "Jira", Ct);
        await Tasks.CreateAsync(new TaskInput(await InternalClientIdAsync(), jira.Id, "WT-1", "Story", null, null), Ct);

        var summary = (await Lookups.GetAsync(LookupKind.TaskSystem, cancellationToken: Ct)).Single(l => l.Id == jira.Id);
        Assert.Equal(1, summary.UsageCount);
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Lookups.DeleteAsync(LookupKind.TaskSystem, jira.Id, Ct));
    }

    [Fact]
    public async Task UnusedCustomItem_CanBeDeleted()
    {
        var jira = await Lookups.CreateAsync(LookupKind.TaskSystem, "Jira", Ct);
        await Lookups.DeleteAsync(LookupKind.TaskSystem, jira.Id, Ct);
        Assert.DoesNotContain(await Lookups.GetAsync(LookupKind.TaskSystem, cancellationToken: Ct), l => l.Id == jira.Id);
    }

    [Fact]
    public async Task Move_ReordersAndIgnoresOutOfRange()
    {
        var before = (await Lookups.GetAsync(LookupKind.TaskSystem, cancellationToken: Ct)).Select(l => l.Name).ToList();

        await Lookups.MoveAsync(LookupKind.TaskSystem, await LookupIdAsync(LookupKind.TaskSystem, "CRM"), -1, Ct);
        await Lookups.MoveAsync(LookupKind.TaskSystem, await LookupIdAsync(LookupKind.TaskSystem, "CRM"), -1, Ct); // already first

        var after = (await Lookups.GetAsync(LookupKind.TaskSystem, cancellationToken: Ct)).Select(l => l.Name).ToList();
        Assert.Equal("CRM", after[0]);
        Assert.Equal(before[0], after[1]);
        Assert.Equal(before.Count, after.Count);
    }
}
