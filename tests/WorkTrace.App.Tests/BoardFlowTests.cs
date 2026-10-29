using System.IO;
using WorkTrace.App.Shell;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;

namespace WorkTrace.App.Tests;

/// <summary>Board and Inbox view-model flows against a real SQLite database (capture, drag-and-drop, Undo, auto-save, attachments).</summary>
public class BoardFlowTests
{
    private static CardFields Fields(string title, BoardCategory? category = BoardCategory.Feature) =>
        new(title, category, BoardPriority.Normal, null, null, null, null);

    private static IReadOnlyList<string> Titles(BoardViewModel vm, BoardStatus status) =>
        [.. vm.Columns.Single(c => c.Status == status).Cards.Select(c => c.Title)];

    [Fact]
    public Task Capture_GoesToInbox_ThenToBoard_AndUndoBringsItBack() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var inbox = ctx.Get<InboxViewModel>();
        var board = ctx.Get<BoardViewModel>();
        await inbox.ActivateAsync(null);
        await board.ActivateAsync(null);

        inbox.CaptureText = "Ask Peter about the export";
        inbox.SetCaptureImage([1, 2, 3]);
        await inbox.CaptureCommand.ExecuteAsync(null);

        var item = Assert.Single(inbox.Items);
        Assert.Null(inbox.CaptureText);
        Assert.False(inbox.HasCaptureImage);
        Assert.Equal(1, item.AttachmentCount);
        Assert.All(board.Columns, c => Assert.Empty(c.Cards));

        await inbox.ToBoardCommand.ExecuteAsync(item);
        await WpfTestHost.SettleAsync();
        Assert.Empty(inbox.Items);
        Assert.Equal(["Ask Peter about the export"], Titles(board, BoardStatus.Backlog)); // board reloaded via the session

        await inbox.UndoCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync();
        Assert.Single(inbox.Items);
        Assert.Empty(Titles(board, BoardStatus.Backlog));
    });

    [Fact]
    public Task DragAndDrop_MovesAndReorders_AndUndoReverses() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IBoardService>();
        var a = await service.CreateAsync(Fields("A"), BoardStatus.Todo);
        var b = await service.CreateAsync(Fields("B"), BoardStatus.Todo);
        var c = await service.CreateAsync(Fields("C"), BoardStatus.Todo);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);

        await board.MoveCardAsync(c.Id, BoardStatus.Todo, a.Id);          // drop C above A
        Assert.Equal(["C", "A", "B"], Titles(board, BoardStatus.Todo));
        await board.MoveCardAsync(a.Id, BoardStatus.Doing, null);         // drop A into In progress
        Assert.Equal(["C", "B"], Titles(board, BoardStatus.Todo));
        Assert.Equal(["A"], Titles(board, BoardStatus.Doing));
        await board.MoveCardAsync(b.Id, BoardStatus.Todo, b.Id);          // dropped onto itself: no change
        Assert.Equal(["C", "B"], Titles(board, BoardStatus.Todo));

        await board.UndoCommand.ExecuteAsync(null);
        await board.UndoCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync();
        Assert.Equal(["A", "B", "C"], Titles(board, BoardStatus.Todo));
        Assert.False(board.Session.CanUndo);
    });

    [Fact]
    public Task AltArrows_MoveBetweenColumnsAndWithinAColumn() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IBoardService>();
        await service.CreateAsync(Fields("A"), BoardStatus.Todo);
        await service.CreateAsync(Fields("B"), BoardStatus.Todo);
        await service.CreateAsync(Fields("X"), BoardStatus.Doing);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);

        var todo = board.Columns.Single(c => c.Status == BoardStatus.Todo);
        await board.MoveByKeyAsync(todo.Cards[0], 0, 1);
        Assert.Equal(["B", "A"], Titles(board, BoardStatus.Todo));
        await board.MoveByKeyAsync(todo.Cards[1], 1, 0);
        Assert.Equal(["A", "X"], Titles(board, BoardStatus.Doing)); // lands at the top of the next column
        await board.MoveByKeyAsync(todo.Cards[0], -1, 0);
        Assert.Equal(["B"], Titles(board, BoardStatus.Backlog));
    });

    [Fact]
    public Task MovingToDone_AsksForAcceptance() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var card = await ctx.Get<IBoardService>().CreateAsync(Fields("Feature"), BoardStatus.Review);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);

        ctx.Dialogs.Answer = false;
        await board.MoveCardAsync(card.Id, BoardStatus.Done, null);
        Assert.Equal(["Feature"], Titles(board, BoardStatus.Review));

        ctx.Dialogs.Answer = true;
        await board.MoveCardAsync(card.Id, BoardStatus.Done, null);
        Assert.Equal(["Feature"], Titles(board, BoardStatus.Done));
        Assert.Equal(["Accept as done?", "Accept as done?"], ctx.Dialogs.Asked);
        Assert.Contains("Accepted", (await ctx.Get<IBoardService>().GetCardAsync(card.Id)).Events[^1].Note, StringComparison.Ordinal);
    });

    [Fact]
    public Task CardPanel_AutoSaves_AndUndoRestoresTheText() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var card = await ctx.Get<IBoardService>().CreateAsync(Fields("Original"), BoardStatus.Todo);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);

        board.SelectedCard = board.Columns.Single(c => c.Status == BoardStatus.Todo).Cards[0];
        await WpfTestHost.SettleAsync(10);
        var detail = Assert.IsType<CardDetailViewModel>(board.Detail);

        detail.Title = "Renamed";
        detail.Notes = "Typed notes";
        Assert.Equal("Saving…", detail.SaveState);
        await Task.Delay(CardDetailViewModel.AutoSaveDelay * 2);
        await detail.FlushAsync();
        await WpfTestHost.SettleAsync();

        Assert.Equal("Saved", detail.SaveState);
        Assert.Equal("Renamed", (await ctx.Get<IBoardService>().GetCardAsync(card.Id)).Title);
        Assert.Equal(["Renamed"], Titles(board, BoardStatus.Todo)); // the tile updates in place

        detail.Title = "";
        await detail.FlushAsync();
        Assert.NotNull(detail.TitleError);
        Assert.Equal("Renamed", (await ctx.Get<IBoardService>().GetCardAsync(card.Id)).Title);
        detail.Title = "Renamed";
        await detail.FlushAsync();

        await board.UndoCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync(10);
        var restored = await ctx.Get<IBoardService>().GetCardAsync(card.Id);
        Assert.Equal(("Original", ""), (restored.Title, restored.Notes));
        Assert.Equal("Original", board.Detail!.Title); // the open panel follows the undo
    });

    [Fact]
    public Task CardPanel_LinksDependenciesCommentsAndTask() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IBoardService>();
        var blocker = await service.CreateAsync(Fields("Blocker"), BoardStatus.Todo);
        var card = await service.CreateAsync(Fields("Card"), BoardStatus.Todo);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(new OpenCardRequest(card.Id));
        var detail = board.Detail!;

        detail.NewLinkUrl = "javascript:alert(1)";
        await detail.AddLinkCommand.ExecuteAsync(null);
        Assert.NotNull(detail.LinkError);
        detail.NewLinkUrl = "https://example.com/spec";
        await detail.AddLinkCommand.ExecuteAsync(null);
        var link = Assert.Single(detail.Links);
        detail.OpenLinkCommand.Execute(link);
        Assert.Equal(["https://example.com/spec"], ctx.Files.Opened);

        detail.DependencyKey = blocker.Id;
        await WpfTestHost.SettleAsync(10);
        Assert.Equal("Blocker", Assert.Single(detail.BlockedBy).Title);
        Assert.True(board.Columns.Single(c => c.Status == BoardStatus.Todo).Cards.Single(c => c.Id == card.Id).IsBlocked);

        detail.NewComment = "Root cause found";
        await detail.AddCommentCommand.ExecuteAsync(null);
        Assert.Equal("Root cause found", Assert.Single(detail.Comments).Text);

        await detail.CreateTaskCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync(10);
        Assert.NotNull((await service.GetCardAsync(card.Id)).WorkTaskTitle);
        Assert.NotEqual(PickerOption.NoneKey, detail.TaskKey);
    });

    [Fact]
    public Task Attachments_BrowseCaptionAndRemove() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var card = await ctx.Get<IBoardService>().CreateAsync(Fields("Card"), BoardStatus.Todo);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(new OpenCardRequest(card.Id));
        var detail = board.Detail!;
        var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, [0x89, 0x50, 0x4E, 0x47]);

        ctx.Files.FilesToOpen.Add(file);
        await detail.BrowseAttachmentsCommand.ExecuteAsync(null);
        var attachment = Assert.Single(detail.Attachments);
        Assert.True(attachment.IsImage);
        Assert.True(File.Exists(attachment.FullPath));

        attachment.Caption = "Login error";
        await detail.SaveCaptionCommand.ExecuteAsync(attachment);
        Assert.Equal("Login error", (await ctx.Get<IBoardService>().GetCardAsync(card.Id)).Attachments[0].Caption);

        await detail.RemoveAttachmentCommand.ExecuteAsync(detail.Attachments[0]);
        Assert.Empty(detail.Attachments);
        Assert.False(File.Exists(attachment.FullPath));
        File.Delete(file);
    });

    [Fact]
    public Task Archive_HidesAndRestores() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IBoardService>();
        await service.CreateAsync(Fields("Done 1", BoardCategory.Testing), BoardStatus.Done);
        await service.CreateAsync(Fields("Done 2", BoardCategory.Testing), BoardStatus.Done);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);

        await board.ArchiveDoneCommand.ExecuteAsync(null);
        Assert.Empty(Titles(board, BoardStatus.Done));

        board.ShowArchive = true;
        await WpfTestHost.SettleAsync(10);
        Assert.Equal(2, board.ArchiveItems.Count);
        board.SearchText = "done 2";
        await Task.Delay(300);
        await WpfTestHost.SettleAsync(10);
        var item = Assert.Single(board.ArchiveItems);

        await board.RestoreCommand.ExecuteAsync(item);
        board.ShowArchive = false;
        board.SearchText = null;
        await Task.Delay(300);
        await WpfTestHost.SettleAsync(10);
        Assert.Equal(["Done 2"], Titles(board, BoardStatus.Done));
    });

    [Fact]
    public Task Backup_ThenRestoreWithPreview_KeepsOrOverwritesLocalEdits() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var service = ctx.Get<IBoardService>();
        var card = await service.CreateAsync(Fields("From backup"), BoardStatus.Todo);
        var board = ctx.Get<BoardViewModel>();
        await board.ActivateAsync(null);
        board.ShowBackup = true;
        await WpfTestHost.SettleAsync(10);
        var backup = board.Backup;
        Assert.False(board.ShowColumns);
        Assert.Single(backup.Months);
        Assert.True(backup.Months[0].Changed);

        var zip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        ctx.Files.SavePath = zip;
        await backup.BackupCompleteCommand.ExecuteAsync(null);
        Assert.True(File.Exists(zip));
        Assert.False(backup.Months[0].Changed);
        Assert.Contains("database", backup.LastResult, StringComparison.Ordinal);

        ctx.Time.Advance(TimeSpan.FromMinutes(5));
        await service.UpdateFieldsAsync(card.Id, Fields("Edited later"));
        await backup.PreviewRestoreAsync(zip);
        var item = Assert.Single(backup.RestoreItems);
        Assert.True(item.CanOverwrite);
        Assert.True(backup.HasRestorePreview);

        await backup.ApplyRestoreCommand.ExecuteAsync(null); // not ticked: the later edit is kept
        Assert.Equal("Edited later", (await service.GetCardAsync(card.Id)).Title);

        await backup.PreviewRestoreAsync(zip);
        backup.RestoreItems[0].Overwrite = true;
        await backup.ApplyRestoreCommand.ExecuteAsync(null);
        Assert.Equal("From backup", (await service.GetCardAsync(card.Id)).Title);
        Assert.False(backup.HasRestorePreview);
        await WpfTestHost.SettleAsync(10);
        board.ShowBackup = false;
        await WpfTestHost.SettleAsync(10);
        Assert.Equal(["From backup"], Titles(board, BoardStatus.Todo));
        File.Delete(zip);
    });

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(10, 0)]
    [InlineData(30, 1)]
    [InlineData(60, 1)]
    [InlineData(70, 2)]
    [InlineData(500, 2)]
    public void InsertionIndex_UsesRowMiddles(double y, int expected) =>
        Assert.Equal(expected, BoardDragDrop.InsertionIndex([(0, 40), (44, 40)], y));
}
