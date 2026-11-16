using WorkTrace.App.Shell;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Management;

namespace WorkTrace.App.Tests;

/// <summary>End-to-end view-model flows against a real SQLite database.</summary>
public class ViewModelFlowTests
{
    [Fact]
    public Task MinorTaskReminder_Edit_OpensTheEditor_AndSavingUpdatesTheReminderState() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IMinorTaskService>();
        var now = ctx.Time.GetUtcNow().UtcDateTime;
        var due = await service.CreateAsync(new MinorTaskInput("Call the accountant", null, null, now.AddMinutes(-5)));
        await service.SnoozeAsync(due.Id, now.AddMinutes(30)); // what the reminder does before showing its popup
        var other = await service.CreateAsync(new MinorTaskInput("Order toner", null, null, now.AddMinutes(-5)));
        await service.SnoozeAsync(other.Id, now.AddMinutes(30));

        // The popup's Edit button navigates with this request.
        var tasks = ctx.Get<TasksViewModel>();
        await tasks.ActivateAsync(new EditMinorTaskRequest(due.Id));
        Assert.True(tasks.MinorGroup.IsExpanded);
        Assert.Equal(due.Id, tasks.Minor.Editing?.Id);

        // Description and a later reminder: the snooze is replaced by the new reminder time, so it does not pop up again now.
        tasks.Minor.EditDescription = "Ask about the VAT return";
        tasks.Minor.EditReminderDate = DateTime.Today.AddDays(1);
        tasks.Minor.EditReminderTime = "10:00";
        await tasks.Minor.SaveEditCommand.ExecuteAsync(null);
        Assert.Null(tasks.Minor.EditError);
        var saved = (await service.GetAsync(includeCompleted: true)).Single(m => m.Id == due.Id);
        Assert.Equal(("Ask about the VAT return", (DateTime?)null), (saved.Description, saved.SnoozedUntilUtc));
        Assert.DoesNotContain(await service.GetDueAsync(now.AddHours(1)), m => m.Id == due.Id);

        // Done in the editor: completed, never reminded again.
        Assert.True(tasks.Minor.EditById(other.Id));
        tasks.Minor.EditCompleted = true;
        await tasks.Minor.SaveEditCommand.ExecuteAsync(null);
        Assert.True((await service.GetAsync(includeCompleted: true)).Single(m => m.Id == other.Id).IsCompleted);
        Assert.DoesNotContain(await service.GetDueAsync(now.AddDays(3)), m => m.Id == other.Id);
    });

    [Fact]
    public Task TimesheetLayout_IsRememberedAcrossSessions_AndClamped() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TimesheetViewModel>();
        await vm.ActivateAsync(null);
        Assert.Equal((0.5, false), (vm.TasksShare, vm.DockHoursToBottom)); // 50/50, not docked by default

        await vm.SaveTasksShareAsync(0.3);
        vm.DockHoursToBottom = true;
        await vm.SaveDockedHoursHeightAsync(260);
        await WpfTestHost.SettleAsync();

        var stored = await ctx.Get<WorkTrace.Core.Settings.ISettingsStore>().LoadAsync(); // read back from settings.json
        Assert.Equal((0.3, true, 260d), (stored.Reporting.TimesheetTasksShare, stored.Reporting.TimesheetDockHoursToBottom, stored.Reporting.TimesheetDockedHoursHeight));

        await vm.SaveTasksShareAsync(0.99);
        Assert.Equal(TimesheetViewModel.MaxTasksShare, vm.TasksShare); // a readable minimum is kept for the other section
    });

    [Fact]
    public Task NewTask_SavesAndSelectsIt() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        await vm.ActivateAsync(null);

        vm.NewTaskCommand.Execute(null);
        Assert.True(vm.Editor!.IsNew);
        Assert.NotNull(vm.Editor.ClientId); // defaults to the first active client (Internal)
        vm.Editor.Title = "  Weekly planning ";
        vm.Editor.ExternalId = "0007";
        Assert.False(vm.IsListEnabled);

        await vm.SaveCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Items);
        Assert.Equal("Weekly planning", item.Title);
        Assert.Equal("0007", item.ExternalId);
        Assert.Same(item, vm.SelectedItem);
        Assert.False(vm.Editor!.IsNew);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(NotificationKind.Success, ctx.Get<INotificationService>().Current?.Kind);
    });

    [Fact]
    public Task Validation_ShowsFieldErrors_AndPersistsNothing() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        await vm.ActivateAsync(null);

        vm.NewTaskCommand.Execute(null);
        vm.Editor!.Title = "Something";
        vm.Editor.ClientId = null;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Editor.ClientError);
        Assert.Empty(await ctx.Get<ITaskService>().SearchAsync(new TaskQuery()));

        vm.Editor.ClientId = vm.Editor.ClientOptions[0].Id;
        Assert.Null(vm.Editor.ClientError);
    });

    [Fact]
    public Task Discard_RestoresOriginalValues_AndUnlocksList() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        var client = (await ctx.Get<IClientService>().GetClientsAsync())[0];
        await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Original", null, null));
        await vm.ActivateAsync(null);

        vm.SelectedItem = vm.Items[0];
        vm.Editor!.Title = "Edited";
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.IsListEnabled);

        vm.DiscardCommand.Execute(null);

        Assert.Equal("Original", vm.Editor!.Title);
        Assert.True(vm.IsListEnabled);
    });

    [Fact]
    public Task Archive_CanBeUndoneFromNotification() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        var client = (await ctx.Get<IClientService>().GetClientsAsync())[0];
        var task = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Archive me", null, null));
        await vm.ActivateAsync(null);
        vm.SelectedItem = vm.Items[0];

        await vm.ToggleArchiveCommand.ExecuteAsync(null);
        Assert.True((await ctx.Get<ITaskService>().GetAsync(task.Id)).IsArchived);

        var notification = ctx.Get<INotificationService>().Current!;
        Assert.Equal("Undo", notification.ActionText);
        await notification.Action!.ExecuteAsync(null);

        Assert.False((await ctx.Get<ITaskService>().GetAsync(task.Id)).IsArchived);
    });

    [Fact]
    public Task CreateClientFromTypedText_KeepsTheRestOfTheForm() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        await vm.ActivateAsync(null);
        vm.NewTaskCommand.Execute(null);
        vm.Editor!.Title = "Kick-off";
        vm.Editor.ExternalId = "CRM-12";
        vm.Editor.ClientId = null;
        vm.Editor.ClientText = "Fabrikam";
        Assert.True(vm.Editor.CanCreateClient);

        await vm.CreateClientFromTextCommand.ExecuteAsync(null);

        var created = (await ctx.Get<IClientService>().GetClientsAsync()).Single(c => c.Name == "Fabrikam");
        Assert.Equal(created.Id, vm.Editor!.ClientId);
        Assert.Equal("Kick-off", vm.Editor.Title);
        Assert.Equal("CRM-12", vm.Editor.ExternalId);
        Assert.False(vm.Editor.CanCreateClient);
    });

    [Fact]
    public Task DeletingUsedTask_OffersArchiveInstead() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        var client = (await ctx.Get<IClientService>().GetClientsAsync())[0];
        var unused = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Unused", null, null));
        await vm.ActivateAsync(null);
        vm.SelectedItem = vm.Items.Single(i => i.Id == unused.Id);

        ctx.Dialogs.Answer = true;
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(["Delete task"], ctx.Dialogs.Asked);
        Assert.Empty(vm.Items);
    });

    [Fact]
    public Task Clients_DeactivateAndUndo() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var created = await ctx.Get<IClientService>().CreateAsync(new ClientInput("Northwind", null, false));
        var vm = ctx.Get<ClientsViewModel>();
        await vm.ActivateAsync(null);
        vm.SelectedItem = vm.Items.Single(i => i.Id == created.Id);

        await vm.ToggleActiveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(await ctx.Get<IClientService>().GetClientsForPickerAsync(), c => c.Id == created.Id);
        Assert.False(vm.SelectedItem!.IsActive);

        await ctx.Get<INotificationService>().Current!.Action!.ExecuteAsync(null);
        Assert.Contains(await ctx.Get<IClientService>().GetClientsForPickerAsync(), c => c.Id == created.Id);
    });

    [Fact]
    public Task Clients_DuplicateNameShowsInlineError() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<ClientsViewModel>();
        await vm.ActivateAsync(null);
        vm.NewClientCommand.Execute(null);
        vm.Editor!.Name = "internal";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(vm.Editor!.NameError);
        Assert.True(vm.Editor.IsNew);
    });

    [Fact]
    public Task Lists_AddRenameHideAndReorder() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<ListsViewModel>();
        await vm.ActivateAsync(null);
        var systems = vm.TaskSystems;

        systems.NewName = "Jira";
        await systems.AddCommand.ExecuteAsync(null);
        Assert.Equal("Jira", systems.Items[^1].Name);
        Assert.Null(systems.NewName);

        var jira = systems.Items[^1];
        jira.EditName = "Jira Cloud";
        await jira.CommitRenameCommand.ExecuteAsync(null);
        await jira.CommitRenameCommand.ExecuteAsync(null); // second commit (focus loss) is ignored
        Assert.Equal("Jira Cloud", systems.Items[^1].Name);

        await systems.Items[^1].MoveUpCommand.ExecuteAsync(null);
        Assert.Equal("Jira Cloud", systems.Items[^2].Name);

        await systems.Items[^2].ToggleActiveCommand.ExecuteAsync(null);
        Assert.False(systems.Items[^2].IsActive);

        systems.NewName = "crm";
        await systems.AddCommand.ExecuteAsync(null);
        Assert.NotNull(systems.NewNameError);
    });

    [Fact]
    public Task ReturningToTasks_RefreshesEditorPickers() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        var client = (await ctx.Get<IClientService>().GetClientsAsync())[0];
        await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Task", null, null));
        await vm.ActivateAsync(null);
        vm.SelectedItem = vm.Items[0];

        await ctx.Get<IClientService>().CreateAsync(new ClientInput("Added elsewhere", null, false));
        await vm.ActivateAsync(null);

        Assert.Contains(vm.Editor!.ClientOptions, o => o.Name == "Added elsewhere");
        Assert.False(vm.HasUnsavedChanges);
    });

    [Fact]
    public Task Editor_StartsWithClientNameAsText() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<TasksViewModel>();
        await vm.ActivateAsync(null);
        vm.NewTaskCommand.Execute(null);

        Assert.Equal("Internal", vm.Editor!.ClientText);
        Assert.False(vm.Editor.CanCreateClient);
        Assert.False(vm.Editor.IsDirty);
    });
}
