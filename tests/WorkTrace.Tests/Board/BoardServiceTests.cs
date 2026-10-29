using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Storage;
using WorkTrace.Tests.Management;

namespace WorkTrace.Tests.Board;

public class BoardServiceTests : ManagementTestBase
{
    private IBoardService Board => Services.GetRequiredService<IBoardService>();

    private static CardFields Fields(string title, BoardCategory? category = BoardCategory.Feature, string? notes = null) =>
        new(title, category, BoardPriority.Normal, null, null, notes, null);

    private async Task<IReadOnlyList<string>> ColumnAsync(BoardStatus status) =>
        [.. (await Board.GetCardsAsync(new CardFilter(Statuses: [status]), Ct)).Select(c => c.Title)];

    [Fact]
    public async Task Create_AssignsIncreasingKeys_AndRecordsCreation()
    {
        var a = await Board.CreateAsync(Fields(" First "), BoardStatus.Todo, Ct);
        var b = await Board.CreateAsync(Fields("Second"), BoardStatus.Todo, Ct);

        Assert.Equal("WT-001", a.Key);
        Assert.Equal("WT-002", b.Key);
        Assert.Equal("First", a.Title);
        Assert.Equal(BoardEventType.Created, Assert.Single(a.Events).Type);
        Assert.Equal(["First", "Second"], await ColumnAsync(BoardStatus.Todo));
    }

    [Fact]
    public async Task Create_RequiresTitle()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => Board.CreateAsync(Fields("  "), BoardStatus.Todo, Ct));
        Assert.NotNull(ex.Errors.For(nameof(CardFields.Title)));
    }

    [Fact]
    public async Task Capture_GoesToInbox_AndUrlBecomesTitleAndLink()
    {
        var note = await Board.CaptureAsync("Ask Peter about the export", cancellationToken: Ct);
        var link = await Board.CaptureAsync("https://example.com/some/page", cancellationToken: Ct);

        Assert.Equal(BoardStatus.Inbox, note.Status);
        Assert.Null(note.Summary.Category);
        Assert.Equal("example.com/some/page", link.Title);
        Assert.Equal(new LinkInput("example.com/some/page", "https://example.com/some/page"), Assert.Single(link.Links));
        Assert.Empty(await ColumnAsync(BoardStatus.Todo));
        Assert.Equal(2, (await Board.GetCardsAsync(new CardFilter(Statuses: [BoardStatus.Inbox]), Ct)).Count);
        await Assert.ThrowsAsync<DomainValidationException>(() => Board.CaptureAsync(" ", cancellationToken: Ct));
    }

    [Fact]
    public async Task Move_ReordersWithinAndBetweenColumns_AndRecordsStatusHistory()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        var b = await Board.CreateAsync(Fields("B"), BoardStatus.Todo, Ct);
        var c = await Board.CreateAsync(Fields("C"), BoardStatus.Todo, Ct);

        await Board.MoveAsync(c.Id, BoardStatus.Todo, a.Id, cancellationToken: Ct);
        Assert.Equal(["C", "A", "B"], await ColumnAsync(BoardStatus.Todo));

        await Board.MoveAsync(a.Id, BoardStatus.Doing, null, cancellationToken: Ct);
        Assert.Equal(["C", "B"], await ColumnAsync(BoardStatus.Todo));
        Assert.Equal(["A"], await ColumnAsync(BoardStatus.Doing));

        var history = (await Board.GetCardAsync(a.Id, Ct)).Events.Last();
        Assert.Equal((BoardEventType.Status, BoardStatus.Todo, BoardStatus.Doing), (history.Type, history.From, history.To));

        // Dropping a card where it already is changes nothing.
        Assert.Null(await Board.MoveAsync(c.Id, BoardStatus.Todo, b.Id, cancellationToken: Ct));
    }

    [Fact]
    public async Task Move_RenumbersColumn_WhenTheGapIsExhausted()
    {
        var first = await Board.CreateAsync(Fields("first"), BoardStatus.Todo, Ct);
        var last = await Board.CreateAsync(Fields("last"), BoardStatus.Todo, Ct);
        var moving = await Board.CreateAsync(Fields("m0"), BoardStatus.Backlog, Ct);
        await Board.MoveAsync(moving.Id, BoardStatus.Todo, last.Id, cancellationToken: Ct);

        // Keep inserting directly after "first": each insert halves the same gap until it must renumber.
        for (var i = 1; i < 70; i++)
        {
            var card = await Board.CreateAsync(Fields($"m{i}"), BoardStatus.Backlog, Ct);
            var column = await Board.GetCardsAsync(new CardFilter(Statuses: [BoardStatus.Todo]), Ct);
            await Board.MoveAsync(card.Id, BoardStatus.Todo, column[1].Id, cancellationToken: Ct);
        }

        var titles = await ColumnAsync(BoardStatus.Todo);
        Assert.Equal("first", titles[0]);
        Assert.Equal("m69", titles[1]);
        Assert.Equal("m0", titles[^2]);
        Assert.Equal("last", titles[^1]);
        Assert.Equal(72, titles.Distinct().Count());
        _ = first;
    }

    [Fact]
    public async Task MoveToDone_NeedsAcceptance_ForUserFacingWork()
    {
        var feature = await Board.CreateAsync(Fields("Feature"), BoardStatus.Review, Ct);
        var testing = await Board.CreateAsync(Fields("Tests", BoardCategory.Testing), BoardStatus.Review, Ct);

        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.MoveAsync(feature.Id, BoardStatus.Done, null, cancellationToken: Ct));
        await Board.MoveAsync(feature.Id, BoardStatus.Done, null, "Works on my machine", Ct);
        await Board.MoveAsync(testing.Id, BoardStatus.Done, null, cancellationToken: Ct);

        Assert.Equal("Works on my machine", (await Board.GetCardAsync(feature.Id, Ct)).Events.Last().Note);
        Assert.Equal(["Feature", "Tests"], await ColumnAsync(BoardStatus.Done));
    }

    [Fact]
    public async Task Undo_RestoresFieldsStatusOrderAndArchive()
    {
        var a = await Board.CreateAsync(Fields("A", notes: "old"), BoardStatus.Todo, Ct);
        var b = await Board.CreateAsync(Fields("B"), BoardStatus.Todo, Ct);
        var history = new BoardUndoHistory();

        history.Push(await Board.UpdateFieldsAsync(a.Id, Fields("A", notes: "ne"), Ct));
        history.Push(await Board.UpdateFieldsAsync(a.Id, Fields("A", notes: "new"), Ct));
        history.Push(await Board.MoveAsync(a.Id, BoardStatus.Todo, null, cancellationToken: Ct));
        history.Push(await Board.MoveAsync(b.Id, BoardStatus.Doing, null, cancellationToken: Ct));
        history.Push(await Board.SetArchivedAsync(a.Id, true, Ct));
        Assert.Equal(["B"], await ColumnAsync(BoardStatus.Doing));

        while (history.Pop() is { } edit)
        {
            await Board.UndoAsync(edit, Ct);
        }

        var restored = await Board.GetCardAsync(a.Id, Ct);
        Assert.Equal("old", restored.Notes); // consecutive field edits undo as one step
        Assert.False(restored.Summary.IsArchived);
        Assert.Equal(["A", "B"], await ColumnAsync(BoardStatus.Todo));
        Assert.Contains(restored.Events, e => e.Type == BoardEventType.Restored);
    }

    [Fact]
    public async Task Undo_RefusesWhenTheCardChangedSince()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        var edit = await Board.UpdateFieldsAsync(a.Id, Fields("A2"), Ct);
        await Board.UpdateFieldsAsync(a.Id, Fields("A3"), Ct);

        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.UndoAsync(edit!, Ct));
        Assert.Equal("A3", (await Board.GetCardAsync(a.Id, Ct)).Title);
    }

    [Fact]
    public async Task Undo_RestoresLinksAndDependencies()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        var b = await Board.CreateAsync(Fields("B"), BoardStatus.Todo, Ct);
        await Board.SetLinksAsync(a.Id, [new LinkInput("Docs", "https://example.com/docs")], Ct);
        var links = await Board.SetLinksAsync(a.Id, [new LinkInput(null, "https://example.com/other")], Ct);
        var deps = await Board.SetBlockedByAsync(a.Id, [b.Id], Ct);

        Assert.True((await Board.GetCardAsync(a.Id, Ct)).Summary.IsBlocked);
        await Board.UndoAsync(deps!, Ct);
        await Board.UndoAsync(links!, Ct);

        var card = await Board.GetCardAsync(a.Id, Ct);
        Assert.Empty(card.Summary.BlockedBy);
        Assert.Equal(new LinkInput("Docs", "https://example.com/docs"), Assert.Single(card.Links));
    }

    [Fact]
    public async Task Dependencies_RejectSelfCyclesAndUnknownCards()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        var b = await Board.CreateAsync(Fields("B"), BoardStatus.Todo, Ct);
        var c = await Board.CreateAsync(Fields("C"), BoardStatus.Todo, Ct);
        await Board.SetBlockedByAsync(a.Id, [b.Id], Ct);
        await Board.SetBlockedByAsync(b.Id, [c.Id], Ct);

        await Assert.ThrowsAsync<DomainValidationException>(() => Board.SetBlockedByAsync(c.Id, [a.Id], Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => Board.SetBlockedByAsync(c.Id, [c.Id], Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => Board.SetBlockedByAsync(c.Id, [999], Ct));

        await Board.MoveAsync(b.Id, BoardStatus.Done, null, "ok", Ct);
        Assert.False((await Board.GetCardAsync(a.Id, Ct)).Summary.IsBlocked);
    }

    [Fact]
    public async Task Links_MustBeWebAddresses()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        await Assert.ThrowsAsync<DomainValidationException>(() => Board.SetLinksAsync(a.Id, [new LinkInput("x", "javascript:alert(1)")], Ct));
        await Assert.ThrowsAsync<DomainValidationException>(() => Board.SetLinksAsync(a.Id, [new LinkInput("x", "file:///c:/secret")], Ct));
    }

    [Fact]
    public async Task Comments_AreAppendOnly_AndSearchable()
    {
        var a = await Board.CreateAsync(Fields("Export"), BoardStatus.Todo, Ct);
        await Board.CreateAsync(Fields("Other"), BoardStatus.Todo, Ct);
        await Board.AddCommentAsync(a.Id, "Root cause: the Ærø timezone", Ct);

        Assert.Equal("owner", Assert.Single((await Board.GetCardAsync(a.Id, Ct)).Comments).Author);
        var found = await Board.GetCardsAsync(new CardFilter(Text: "aero TIMEZONE"), Ct);
        Assert.Equal("Export", Assert.Single(found).Title);
        Assert.Equal("Export", Assert.Single(await Board.GetCardsAsync(new CardFilter(Text: "wt-001"), Ct)).Title);
    }

    [Fact]
    public async Task Archive_HidesCards_AndRestoreBringsThemBack()
    {
        var a = await Board.CreateAsync(Fields("A", BoardCategory.Testing), BoardStatus.Done, Ct);
        var b = await Board.CreateAsync(Fields("B", BoardCategory.Testing), BoardStatus.Done, Ct);

        Assert.Equal(2, (await Board.ArchiveDoneAsync(Ct)).Count);
        Assert.Empty(await ColumnAsync(BoardStatus.Done));
        var archived = await Board.GetCardsAsync(new CardFilter(Archived: true), Ct);
        Assert.Equal(["A", "B"], archived.Select(c => c.Title));
        Assert.NotNull(archived[0].ArchivedAtUtc);

        await Board.SetArchivedAsync(b.Id, false, Ct);
        Assert.Equal(["B"], await ColumnAsync(BoardStatus.Done));
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.MoveAsync(a.Id, BoardStatus.Todo, null, cancellationToken: Ct));
    }

    [Fact]
    public async Task Card_CanLinkToATrackedTask()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        var task = await Tasks.CreateAsync(new TaskInput(await InternalClientIdAsync(), null, null, "Board work", null, null), Ct);

        await Board.SetWorkTaskAsync(a.Id, task.Id, Ct);
        Assert.Equal("Board work", (await Board.GetCardAsync(a.Id, Ct)).WorkTaskTitle);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Board.SetWorkTaskAsync(a.Id, 999, Ct));
    }

    [Fact]
    public async Task Edits_AreWrittenToTheChangeLog()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        await Board.UpdateFieldsAsync(a.Id, Fields("A", notes: "n"), Ct);

        await using var db = await DbAsync();
        Assert.Equal(2, await db.ChangeLog.CountAsync(e => e.EntityType == nameof(BoardCard) && e.EntityId == a.Id, Ct));
    }

    [Fact]
    public async Task Attachments_AreStoredUnderTheDataDirectory_AndRemovable()
    {
        var a = await Board.CreateAsync(Fields("A"), BoardStatus.Todo, Ct);
        byte[] png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

        var attachment = await Board.AddAttachmentAsync(a.Id, "Screen shot.png", "image/png", png, " login error ", Ct);
        var root = Services.GetRequiredService<AppPaths>().AttachmentsDirectory;

        Assert.StartsWith(root, attachment.FullPath, StringComparison.Ordinal);
        Assert.Equal(png, await File.ReadAllBytesAsync(attachment.FullPath, Ct));
        Assert.Equal("login error", attachment.Caption);
        Assert.Equal(".png", Path.GetExtension(attachment.FullPath));
        Assert.Equal(1, (await Board.GetCardAsync(a.Id, Ct)).Summary.AttachmentCount);

        Assert.Equal("new caption", (await Board.SetAttachmentCaptionAsync(attachment.Id, "new caption", Ct)).Caption);
        await Board.RemoveAttachmentAsync(attachment.Id, Ct);
        Assert.False(File.Exists(attachment.FullPath));
        Assert.Empty((await Board.GetCardAsync(a.Id, Ct)).Attachments);
        await Assert.ThrowsAsync<OperationNotAllowedException>(() => Board.AddAttachmentAsync(a.Id, "x.png", "image/png", [], cancellationToken: Ct));
    }
}
