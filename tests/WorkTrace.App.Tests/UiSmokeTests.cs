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
        await ctx.Get<WorkTrace.Core.Tracking.ICheckInService>().RecordAsync(new WorkTrace.Core.Tracking.CheckInRequest(null, archived.Id, "a note"));
        var missed = await ctx.Get<WorkTrace.Core.Tracking.ITrackingStore>().CreatePromptAsync(ctx.Time.GetUtcNow().UtcDateTime, ctx.Time.GetUtcNow().UtcDateTime, WorkTrace.Core.Entities.CheckInOutcome.Pending);
        await ctx.Get<WorkTrace.Core.Tracking.ITrackingStore>().ResolvePromptAsync(missed, WorkTrace.Core.Entities.CheckInOutcome.Missed, ctx.Time.GetUtcNow().UtcDateTime);
        await ctx.Get<ITaskService>().SetArchivedAsync(archived.Id, true);

        // Board: cards in every column, a blocked card with links, comments and a screenshot, an archived card and Inbox items.
        var board = ctx.Get<WorkTrace.Core.Board.IBoardService>();
        var cards = new List<WorkTrace.Core.Board.CardDetail>();
        foreach (var status in WorkTrace.Core.Board.BoardRules.Columns)
        {
            cards.Add(await board.CreateAsync(new WorkTrace.Core.Board.CardFields($"Card in {status}", WorkTrace.Core.Entities.BoardCategory.Testing,
                WorkTrace.Core.Entities.BoardPriority.High, "Description", "One\nTwo", "Notes", "CI run 1"), status));
        }

        await board.SetBlockedByAsync(cards[1].Id, [cards[0].Id]);
        await board.SetLinksAsync(cards[1].Id, [new WorkTrace.Core.Board.LinkInput("Spec", "https://example.com/spec")]);
        await board.AddCommentAsync(cards[1].Id, "A comment");
        await board.AddAttachmentAsync(cards[1].Id, "shot.png", "image/png", TinyPng());
        await board.AddAttachmentAsync(cards[1].Id, "notes.txt", "text/plain", [65, 66]);
        await board.SetWorkTaskAsync(cards[1].Id, archived.Id);
        var archivedCard = await board.CreateAsync(new WorkTrace.Core.Board.CardFields("Archived card", null, WorkTrace.Core.Entities.BoardPriority.Low, null, null, null, null), WorkTrace.Core.Entities.BoardStatus.Done);
        await board.SetArchivedAsync(archivedCard.Id, true);
        var guide = await ctx.Get<WorkTrace.Core.Guides.IGuideService>().CreateAsync("Release checklist", "Deployment");
        await ctx.Get<WorkTrace.Core.Guides.IGuideService>().UpdateAsync(guide.Id, guide.Title, guide.Category, "Step 1\nStep 2");
        await board.CaptureAsync("Inbox thought");
        await board.CaptureAsync("https://example.com/captured");

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
            Assert.False(tasks.HasUnsavedChanges, $"Rendering the editor must not modify it: {tasks.Editor.ToInput()} vs {tasks.Editor.Original}");
            tasks.SelectedItem = tasks.Items[1];
            await WpfTestHost.SettleAsync();
            Assert.Equal(tasks.Items[1].Id, tasks.Editor!.Id);
            Assert.False(tasks.HasUnsavedChanges, $"Switching tasks must not modify the editor: {tasks.Editor.ToInput()} vs {tasks.Editor.Original}");
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

            // Board: open a rich card, the archive, then the Inbox with an open item.
            window.ViewModel.NavigateTo(AppSection.Board);
            await WpfTestHost.SettleAsync(10);
            var boardPage = ctx.Get<BoardViewModel>();
            Assert.All(boardPage.Columns, c => Assert.NotEmpty(c.Cards)); // incl. cards added automatically for new tasks
            boardPage.SelectedCard = boardPage.Columns[1].Cards.Single(c => c.Id == cards[1].Id);
            await WpfTestHost.SettleAsync(10);
            Assert.Equal(2, boardPage.Detail!.Attachments.Count);
            boardPage.ShowArchive = true;
            await WpfTestHost.SettleAsync(10);
            Assert.NotEmpty(boardPage.ArchiveItems);
            boardPage.ShowArchive = false;
            boardPage.ShowBackup = true;
            await WpfTestHost.SettleAsync(10);
            var zip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
            await ctx.Get<WorkTrace.Core.Board.IBackupService>().CreateAsync(new WorkTrace.Core.Board.BackupRequest(WorkTrace.Core.Board.BackupKind.AllCards), zip);
            await boardPage.Backup.PreviewRestoreAsync(zip);
            await WpfTestHost.SettleAsync(10);
            Assert.NotEmpty(boardPage.Backup.RestoreItems);
            boardPage.Backup.CancelRestoreCommand.Execute(null);
            boardPage.ShowBackup = false;
            System.IO.File.Delete(zip);
            await boardPage.CloseCardCommand.ExecuteAsync(null);
            await WpfTestHost.SettleAsync();

            window.ViewModel.NavigateTo(AppSection.Tasks, new CaptureRequest()); // Inbox lives on the Tasks page
            await WpfTestHost.SettleAsync(10);
            Assert.True(ctx.Get<TasksViewModel>().ShowInbox);
            var inboxPage = ctx.Get<InboxViewModel>();
            Assert.Equal(2, inboxPage.Items.Count);
            inboxPage.SelectedItem = inboxPage.Items[0];
            await WpfTestHost.SettleAsync(10);
            Assert.NotNull(inboxPage.Detail);

            window.ViewModel.NavigateTo(AppSection.Home);
            await WpfTestHost.SettleAsync(10);
            Assert.NotEmpty(ctx.Get<HomeViewModel>().Today);

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

    /// <summary>A valid 1×1 PNG.</summary>
    private static byte[] TinyPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

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
