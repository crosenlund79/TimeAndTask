using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WorkTrace.App.Shell;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Management;

namespace WorkTrace.App.Tests;

/// <summary>
/// Renders the real main window with the Fluent theme and every page, and fails on XAML load
/// errors, unhandled UI exceptions and WPF binding errors (which are otherwise silent).
/// </summary>
public class UiSmokeTests
{
    [Fact]
    public Task MainWindow_RendersEveryPage_WithoutBindingErrors() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var client = await ctx.Get<IClientService>().CreateAsync(new ClientInput("Contoso", "ERP customer", false));
        await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, "BUG-0042", "Invoice posting error", "Notes", null));
        var archived = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Old work", null, null));
        await ctx.Get<ITaskService>().SetArchivedAsync(archived.Id, true);

        WpfTestHost.BindingErrors.Take();
        WpfTestHost.UnhandledExceptions.Clear();

        var window = ctx.Get<MainWindow>();
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.ShowActivated = false;
        window.Show();
        try
        {
            foreach (var section in Enum.GetValues<AppSection>())
            {
                window.ViewModel.NavigateTo(section);
                await WpfTestHost.SettleAsync();
                Assert.NotNull(FindDescendant<UserControl>(window));
            }

            // Exercise the interactive states of the task page: selection, archived rows, new-task form.
            window.ViewModel.NavigateTo(AppSection.Tasks);
            await WpfTestHost.SettleAsync();
            var tasks = ctx.Get<TasksViewModel>();
            tasks.ShowArchived = true;
            await WpfTestHost.SettleAsync(10);
            Assert.Equal(2, tasks.Items.Count);
            tasks.SelectedItem = tasks.Items[0];
            await WpfTestHost.SettleAsync();
            Assert.NotNull(tasks.Editor);
            Assert.False(tasks.HasUnsavedChanges, "Rendering the editor must not modify it (ComboBox write-back).");
            tasks.SelectedItem = tasks.Items[1];
            await WpfTestHost.SettleAsync();
            Assert.Equal(tasks.Items[1].Id, tasks.Editor!.Id);
            Assert.False(tasks.HasUnsavedChanges, "Switching tasks must not modify the editor.");
            tasks.SelectedItem = tasks.Items[0];
            await WpfTestHost.SettleAsync();
            tasks.Editor!.Title = "Changed";
            await WpfTestHost.SettleAsync();
            tasks.DiscardCommand.Execute(null);
            tasks.NewTaskCommand.Execute(null);
            await WpfTestHost.SettleAsync();
            tasks.Editor!.ClientId = null;
            tasks.Editor.ClientText = "Brand new client";
            await WpfTestHost.SettleAsync();

            window.ViewModel.NavigateTo(AppSection.Clients);
            await WpfTestHost.SettleAsync();
            var clients = ctx.Get<ClientsViewModel>();
            clients.SelectedItem = clients.Items[0];
            await WpfTestHost.SettleAsync();
            Assert.False(clients.HasUnsavedChanges);

            ctx.Get<INotificationService>().Show("Toast with action", NotificationKind.Success, "Undo", () => Task.CompletedTask);
            await WpfTestHost.SettleAsync();
        }
        finally
        {
            window.Close();
        }

        Assert.Empty(WpfTestHost.UnhandledExceptions);
        var errors = WpfTestHost.BindingErrors.Take();
        Assert.True(errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors));
    });

    [Fact]
    public Task ConfirmDialog_Loads() => WpfTestHost.RunAsync(() =>
    {
        var dialog = new ConfirmDialog("Delete task", "Delete it?", "Delete", destructive: true);
        Assert.Equal("Delete task", dialog.Title);
        dialog.Close();
        return Task.CompletedTask;
    });

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
