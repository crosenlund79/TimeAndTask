using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.Tests.Management;

public class TaskServiceTests : ManagementTestBase
{
    private async Task<(int ClientId, int D365, int Teams)> SetupAsync()
    {
        var client = await Clients.CreateAsync(new ClientInput("Contoso", null, false), Ct);
        return (client.Id, await LookupIdAsync(LookupKind.TaskSystem, "D365FO"), await LookupIdAsync(LookupKind.CommunicationChannel, "Microsoft Teams"));
    }

    [Fact]
    public async Task Create_PersistsAllFields_AndPreservesExternalIdFormatting()
    {
        var (clientId, d365, teams) = await SetupAsync();

        var task = await Tasks.CreateAsync(new TaskInput(clientId, d365, "  00042-b ", " Invoice posting ", " Notes ", teams), Ct);

        Assert.Equal("00042-b", task.ExternalId);
        Assert.Equal("Invoice posting", task.Title);
        Assert.Equal("Notes", task.Notes);
        Assert.Equal("Contoso", task.ClientName);
        Assert.Equal("D365FO", task.TaskSystemName);
        Assert.Equal("Microsoft Teams", task.DefaultChannelName);
        Assert.False(task.IsArchived);
        Assert.Null(task.LastUsedAtUtc);
    }

    [Fact]
    public async Task Create_RequiresExistingActiveClient()
    {
        var (clientId, _, _) = await SetupAsync();
        await Clients.SetActiveAsync(clientId, false, Ct);

        var inactive = await Assert.ThrowsAsync<DomainValidationException>(() => Tasks.CreateAsync(new TaskInput(clientId, null, null, "X", null, null), Ct));
        Assert.NotNull(inactive.Errors.For(nameof(TaskInput.ClientId)));

        var missing = await Assert.ThrowsAsync<DomainValidationException>(() => Tasks.CreateAsync(new TaskInput(999, null, null, "X", null, null), Ct));
        Assert.NotNull(missing.Errors.For(nameof(TaskInput.ClientId)));
    }

    [Fact]
    public async Task Create_RejectsInactiveTaskSystem_ButExistingReferenceStaysEditable()
    {
        var (clientId, d365, _) = await SetupAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, d365, null, "Existing", null, null), Ct);
        await Lookups.SetActiveAsync(LookupKind.TaskSystem, d365, false, Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() => Tasks.CreateAsync(new TaskInput(clientId, d365, null, "New", null, null), Ct));

        var edited = await Tasks.UpdateAsync(task.Id, new TaskInput(clientId, d365, null, "Existing, renamed", null, null), Ct);
        Assert.Equal("Existing, renamed", edited.Title);
    }

    [Fact]
    public async Task DuplicateExternalIds_AreAllowed_AndReportedAsSimilar()
    {
        var (clientId, d365, _) = await SetupAsync();
        var first = await Tasks.CreateAsync(new TaskInput(clientId, d365, "4711", "First", null, null), Ct);

        var input = new TaskInput(clientId, d365, " 4711 ", "Second", null, null);
        var similar = await Tasks.FindSimilarAsync(input, cancellationToken: Ct);
        var second = await Tasks.CreateAsync(input, Ct);

        Assert.Equal([first.Id], similar.Select(s => s.Id));
        Assert.NotEqual(first.Id, second.Id);
        Assert.Empty(await Tasks.FindSimilarAsync(new TaskInput(clientId, null, "4711", "Other system", null, null), cancellationToken: Ct));
    }

    [Fact]
    public async Task Archive_HidesFromDefaultSearch_AndCanBeRestored()
    {
        var (clientId, _, _) = await SetupAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Old project", null, null), Ct);

        await Tasks.SetArchivedAsync(task.Id, true, Ct);
        Assert.Empty(await Tasks.SearchAsync(new TaskQuery("old"), Ct));
        Assert.Single(await Tasks.SearchAsync(new TaskQuery("old", IncludeArchived: true), Ct));

        var restored = await Tasks.SetArchivedAsync(task.Id, false, Ct);
        Assert.False(restored.IsArchived);
    }

    [Fact]
    public async Task Restore_UnderInactiveClient_IsRefused()
    {
        var (clientId, _, _) = await SetupAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task", null, null), Ct);
        await Tasks.SetArchivedAsync(task.Id, true, Ct);
        await Clients.SetActiveAsync(clientId, false, Ct);

        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Tasks.SetArchivedAsync(task.Id, false, Ct));
    }

    [Fact]
    public async Task Search_ExcludesTasksOfInactiveClients_UnlessFilteredByThatClient()
    {
        var (clientId, _, _) = await SetupAsync();
        await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Hidden later", null, null), Ct);
        await Clients.SetActiveAsync(clientId, false, Ct);

        Assert.Empty(await Tasks.SearchAsync(new TaskQuery("hidden"), Ct));
        Assert.Single(await Tasks.SearchAsync(new TaskQuery("hidden", ClientId: clientId), Ct));
        Assert.Single(await Tasks.SearchAsync(new TaskQuery("hidden", IncludeInactiveClients: true), Ct));
    }

    [Fact]
    public async Task Search_MatchesClientAndExternalId_FromDatabase()
    {
        var (clientId, d365, _) = await SetupAsync();
        var internalId = await InternalClientIdAsync();
        await Tasks.CreateAsync(new TaskInput(clientId, d365, "BUG-0042", "Crash when posting", null, null), Ct);
        await Tasks.CreateAsync(new TaskInput(internalId, null, null, "Team meeting", null, null), Ct);

        Assert.Equal("Crash when posting", (await Tasks.SearchAsync(new TaskQuery("42"), Ct)).Single().Title);
        Assert.Equal("Crash when posting", (await Tasks.SearchAsync(new TaskQuery("contoso crash"), Ct)).Single().Title);
        Assert.Equal(2, (await Tasks.SearchAsync(new TaskQuery(), Ct)).Count);
    }

    [Fact]
    public async Task MarkUsed_UpdatesRecency_WithoutChangeLogOrModifiedAt()
    {
        var (clientId, _, _) = await SetupAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Task", null, null), Ct);
        Time.Advance(TimeSpan.FromHours(1));

        await Tasks.MarkUsedAsync(task.Id, Ct);

        var reloaded = await Tasks.GetAsync(task.Id, Ct);
        Assert.Equal(Time.GetUtcNow().UtcDateTime, reloaded.LastUsedAtUtc);
        Assert.Equal(task.ModifiedAtUtc, reloaded.ModifiedAtUtc);
        await using var db = await DbAsync();
        Assert.Equal(1, await db.ChangeLog.CountAsync(c => c.EntityType == "WorkTask" && c.EntityId == task.Id, Ct));
    }

    [Fact]
    public async Task Delete_UnusedTask_Succeeds_UsedTask_IsRefused()
    {
        var (clientId, _, _) = await SetupAsync();
        var unused = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Unused", null, null), Ct);
        var used = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Used", null, null), Ct);
        await AddObservationAsync(used.Id);

        await Tasks.DeleteAsync(unused.Id, Ct);
        Assert.False(await Tasks.CanDeleteAsync(used.Id, Ct));
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Tasks.DeleteAsync(used.Id, Ct));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Tasks.GetAsync(unused.Id, Ct));
    }

    [Fact]
    public async Task Delete_TaskOnlyReferencedAsOriginalObservationTask_IsRefused()
    {
        var (clientId, _, _) = await SetupAsync();
        var original = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Original", null, null), Ct);
        var corrected = await Tasks.CreateAsync(new TaskInput(clientId, null, null, "Corrected", null, null), Ct);
        var observationId = await AddObservationAsync(original.Id);

        await using (var db = await DbAsync())
        {
            var observation = await db.Observations.SingleAsync(o => o.Id == observationId, Ct);
            observation.TaskId = corrected.Id;
            await db.SaveChangesAsync(Ct);
        }

        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Tasks.DeleteAsync(original.Id, Ct));
    }

    [Fact]
    public async Task Tasks_PersistAcrossRestart()
    {
        var (clientId, d365, _) = await SetupAsync();
        var task = await Tasks.CreateAsync(new TaskInput(clientId, d365, "007", "Persisted", null, null), Ct);

        // A fresh service provider simulates restarting the application.
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await InitializeAsync();

        var reloaded = await Tasks.GetAsync(task.Id, Ct);
        Assert.Equal("007", reloaded.ExternalId);
        Assert.Equal("D365FO", reloaded.TaskSystemName);
    }
}
