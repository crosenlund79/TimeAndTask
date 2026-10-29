using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Board;

internal sealed partial class BoardService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    AppPaths paths,
    TimeProvider timeProvider,
    ILogger<BoardService> logger) : IBoardService
{
    // ---- Queries ----

    public async Task<IReadOnlyList<CardSummary>> GetCardsAsync(CardFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var all = await LoadSummariesAsync(db, cancellationToken);

        var candidates = all.Where(c => c.IsArchived == filter.Archived
                && (filter.Statuses is null || filter.Statuses.Contains(c.Status))
                && (filter.Category is null || c.Category == filter.Category)
                && (filter.Priority is null || c.Priority == filter.Priority))
            .ToList();

        if (Text.Clean(filter.Text) is { } text)
        {
            // A personal board is small: search in memory so matching is accent-insensitive and covers comments.
            var ids = candidates.Select(c => c.Id).ToList();
            var texts = await db.BoardCards.AsNoTracking().Where(c => ids.Contains(c.Id))
                .Select(c => new { c.Id, c.Description, c.Acceptance, c.Notes, c.Evidence, Comments = c.Comments.Select(m => m.Text).ToList() })
                .ToDictionaryAsync(c => c.Id, cancellationToken);
            candidates = [.. candidates.Where(c =>
            {
                var t = texts[c.Id];
                return BoardRules.Matches(text, [c.Key, c.Title, t.Description, t.Acceptance, t.Notes, t.Evidence, .. t.Comments]);
            })];
        }

        return [.. candidates.OrderBy(c => c.Status).ThenBy(c => c.SortOrder).ThenBy(c => c.Number)];
    }

    public async Task<CardDetail> GetCardAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var card = await db.BoardCards.AsNoTracking().AsSplitQuery()
            .Include(c => c.Links).Include(c => c.Comments).Include(c => c.Events).Include(c => c.Attachments).Include(c => c.WorkTask)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Card", id);
        var summary = (await LoadSummariesAsync(db, cancellationToken)).Single(c => c.Id == id);

        return new CardDetail(
            summary,
            card.Description,
            card.Acceptance,
            card.Notes,
            card.Evidence,
            [.. card.Links.OrderBy(l => l.Position).Select(l => new LinkInput(l.Label, l.Url))],
            [.. card.Comments.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id).Select(c => new CommentItem(c.Id, c.Author, c.CreatedAtUtc, c.Text))],
            [.. card.Events.OrderBy(e => e.AtUtc).ThenBy(e => e.Id).Select(e => new CardEventItem(e.Type, e.AtUtc, e.By, e.From, e.To, e.Note))],
            [.. card.Attachments.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).Select(ToItem)],
            card.WorkTask?.Title,
            card.Revision);
    }

    public async Task<IReadOnlyList<CardRef>> GetCardRefsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cards = await db.BoardCards.AsNoTracking().OrderBy(c => c.Number)
            .Select(c => new { c.Id, c.Number, c.Title, c.Status, c.IsArchived }).ToListAsync(cancellationToken);
        return [.. cards.Select(c => new CardRef(c.Id, BoardCard.FormatKey(c.Number), c.Title, c.Status, c.IsArchived))];
    }

    private static async Task<List<CardSummary>> LoadSummariesAsync(WorkTraceDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.BoardCards.AsNoTracking().Select(c => new
        {
            c.Id, c.Number, c.Title, c.Category, c.Priority, c.Status, c.SortOrder, c.IsArchived, c.ArchivedAtUtc,
            c.WorkTaskId, c.CreatedAtUtc, c.ModifiedAtUtc,
            Comments = c.Comments.Count,
            Links = c.Links.Count,
            Attachments = c.Attachments.Count,
            BlockedBy = c.BlockedBy.Select(d => d.BlockedByCardId).ToList(),
        }).ToListAsync(cancellationToken);

        var refs = rows.ToDictionary(r => r.Id, r => new CardRef(r.Id, BoardCard.FormatKey(r.Number), r.Title, r.Status, r.IsArchived));
        return [.. rows.Select(r => new CardSummary(
            r.Id, r.Number, BoardCard.FormatKey(r.Number), r.Title, r.Category, r.Priority, r.Status, r.SortOrder, r.IsArchived,
            r.ArchivedAtUtc, [.. r.BlockedBy.Select(b => refs[b]).OrderBy(b => b.Key, StringComparer.Ordinal)],
            r.Comments, r.Links, r.Attachments, r.WorkTaskId, r.CreatedAtUtc, r.ModifiedAtUtc))];
    }

    // ---- Creating ----

    public async Task<CardDetail> CaptureAsync(string? text, string? url = null, CancellationToken cancellationToken = default)
    {
        var title = Text.Clean(text);
        var link = Text.Clean(url);
        if (link is null && title is not null && BoardRules.IsWebUrl(title) && !title.Contains(' ', StringComparison.Ordinal))
        {
            link = title;
            title = null;
        }

        if (title is null && link is null)
        {
            throw DomainValidationException.ForField(nameof(CardFields.Title), "Type something to capture.");
        }

        var links = link is null ? [] : BoardRules.CleanLinks([new LinkInput(null, link)]);
        title ??= links[0].Label!;
        if (title.Length > BoardCard.TitleMaxLength)
        {
            // Long captures keep their full text in the description.
            var full = title;
            title = full[..(BoardCard.TitleMaxLength - 1)] + "…";
            return await CreateCoreAsync(new CardFields(title, null, BoardPriority.Normal, full, null, null, null), BoardStatus.Inbox, links, cancellationToken);
        }

        return await CreateCoreAsync(new CardFields(title, null, BoardPriority.Normal, null, null, null, null), BoardStatus.Inbox, links, cancellationToken);
    }

    public Task<CardDetail> CreateAsync(CardFields fields, BoardStatus status, CancellationToken cancellationToken = default) =>
        CreateCoreAsync(fields, status, [], cancellationToken);

    private async Task<CardDetail> CreateCoreAsync(CardFields fields, BoardStatus status, IReadOnlyList<LinkInput> links, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fields);
        BoardRules.Validate(fields).ThrowIfInvalid();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = UtcTime.Now(timeProvider);
        var last = await db.BoardCards.Where(c => c.Status == status && !c.IsArchived)
            .MaxAsync(c => (double?)c.SortOrder, cancellationToken);
        var card = new BoardCard
        {
            Number = await AllocateNumberAsync(db, cancellationToken),
            Uid = Guid.NewGuid().ToString(),
            Title = Text.Clean(fields.Title)!,
            Status = status,
            SortOrder = (last ?? 0) + BoardRules.SortStep,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
        };
        ApplyFields(card, fields);
        card.Links.AddRange(links.Select((l, i) => new BoardLink { Position = i, Label = l.Label!, Url = l.Url! }));
        card.Events.Add(NewEvent(BoardEventType.Created, now, to: status));
        db.BoardCards.Add(card);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Board card {CardId} created in {Status}", card.Id, status);
        return await GetCardAsync(card.Id, cancellationToken);
    }

    private static async Task<int> AllocateNumberAsync(WorkTraceDbContext db, CancellationToken cancellationToken)
    {
        var state = await db.BoardState.SingleAsync(cancellationToken);
        var max = await db.BoardCards.MaxAsync(c => (int?)c.Number, cancellationToken) ?? 0;
        var number = Math.Max(state.NextNumber, max + 1);
        state.NextNumber = number + 1;
        return number;
    }

    // ---- Undoable edits ----

    public Task<CardEdit?> UpdateFieldsAsync(int id, CardFields fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        BoardRules.Validate(fields).ThrowIfInvalid();
        return EditAsync(id, BoardUndoHistory.FieldsEdit, (_, card) =>
        {
            ApplyFields(card, fields);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public async Task<CardEdit?> MoveAsync(int id, BoardStatus status, int? beforeCardId, string? acceptance = null, CancellationToken cancellationToken = default)
    {
        return await EditAsync(id, $"Move to {BoardRules.StatusName(status)}", async (db, card) =>
        {
            if (card.IsArchived)
            {
                throw new OperationNotAllowedException($"{card.Key} is archived. Restore it first.");
            }

            if (status == BoardStatus.Done && card.Status != BoardStatus.Done && BoardRules.NeedsOwnerAcceptance(card.Category)
                && Text.Clean(acceptance) is null)
            {
                throw new OperationNotAllowedException($"Moving {card.Key} to Done needs your acceptance.");
            }

            await PlaceAsync(db, card, status, beforeCardId, cancellationToken);
        }, cancellationToken, acceptance);
    }

    /// <summary>Gives the card a sort value in <paramref name="status"/> before <paramref name="beforeCardId"/>; renumbers the column if needed.</summary>
    private static async Task PlaceAsync(WorkTraceDbContext db, BoardCard card, BoardStatus status, int? beforeCardId, CancellationToken cancellationToken)
    {
        var column = await db.BoardCards.Where(c => c.Status == status && !c.IsArchived)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Number).ToListAsync(cancellationToken);
        var currentIndex = column.FindIndex(c => c.Id == card.Id);
        var others = column.Where(c => c.Id != card.Id).ToList();
        var index = beforeCardId is { } before && others.FindIndex(c => c.Id == before) is var i and >= 0 ? i : others.Count;

        if (card.Status == status && currentIndex == index)
        {
            return; // already there
        }

        var value = BoardRules.SortBetween(index > 0 ? others[index - 1].SortOrder : null, index < others.Count ? others[index].SortOrder : null);
        if (value is null)
        {
            for (var n = 0; n < others.Count; n++)
            {
                others[n].SortOrder = (n + 1 + (n >= index ? 1 : 0)) * BoardRules.SortStep;
            }

            value = (index + 1) * BoardRules.SortStep;
        }

        card.Status = status;
        card.SortOrder = value.Value;
    }

    public Task<CardEdit?> SetArchivedAsync(int id, bool isArchived, CancellationToken cancellationToken = default) =>
        EditAsync(id, isArchived ? "Archive" : "Restore", (_, card) =>
        {
            card.IsArchived = isArchived;
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<IReadOnlyList<CardEdit>> ArchiveDoneAsync(CancellationToken cancellationToken = default)
    {
        List<int> ids;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            ids = await db.BoardCards.Where(c => c.Status == BoardStatus.Done && !c.IsArchived).Select(c => c.Id).ToListAsync(cancellationToken);
        }

        var edits = new List<CardEdit>();
        foreach (var id in ids)
        {
            if (await SetArchivedAsync(id, true, cancellationToken) is { } edit)
            {
                edits.Add(edit);
            }
        }

        return edits;
    }

    public Task<CardEdit?> SetLinksAsync(int id, IReadOnlyList<LinkInput> links, CancellationToken cancellationToken = default)
    {
        var cleaned = BoardRules.CleanLinks(links);
        return EditAsync(id, "Links", (db, card) =>
        {
            ReplaceLinks(db, card, cleaned);
            return Task.CompletedTask;
        }, cancellationToken);
    }

    public Task<CardEdit?> SetBlockedByAsync(int id, IReadOnlyList<int> blockedByIds, CancellationToken cancellationToken = default) =>
        EditAsync(id, "Dependencies", async (db, card) =>
        {
            await ValidateBlockedByAsync(db, card.Id, blockedByIds, cancellationToken);
            ReplaceBlockedBy(db, card, blockedByIds);
        }, cancellationToken);

    public Task<CardEdit?> SetWorkTaskAsync(int id, int? workTaskId, CancellationToken cancellationToken = default) =>
        EditAsync(id, "Tracked task", async (db, card) =>
        {
            if (workTaskId is { } taskId && !await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken))
            {
                throw new EntityNotFoundException("Task", taskId);
            }

            card.WorkTaskId = workTaskId;
        }, cancellationToken);

    public async Task<CardEdit> UndoAsync(CardEdit edit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var result = await EditAsync(edit.CardId, $"Undo: {edit.Description}", async (db, card) =>
        {
            if (!StateOf(card).SameAs(edit.After))
            {
                throw new OperationNotAllowedException($"{card.Key} was changed after this edit, so it was not undone.");
            }

            var known = await db.BoardCards.Where(c => edit.Before.BlockedByIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
            Apply(db, card, edit.Before with { BlockedByIds = known });
        }, cancellationToken, acceptance: null, isUndo: true);
        return result ?? throw new OperationNotAllowedException("There was nothing to undo.");
    }

    /// <summary>
    /// Loads the card with its links and dependencies, applies <paramref name="change"/>, records status and
    /// archive transitions in the history, and saves. Returns null when nothing changed.
    /// </summary>
    private async Task<CardEdit?> EditAsync(
        int id,
        string description,
        Func<WorkTraceDbContext, BoardCard, Task> change,
        CancellationToken cancellationToken,
        string? acceptance = null,
        bool isUndo = false)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var card = await db.BoardCards.Include(c => c.Links).Include(c => c.BlockedBy)
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Card", id);

        var before = StateOf(card);
        await change(db, card);
        var after = StateOf(card);
        if (before.SameAs(after))
        {
            return null;
        }

        var now = UtcTime.Now(timeProvider);
        if (before.Status != after.Status)
        {
            var note = after.Status == BoardStatus.Done ? Text.Clean(acceptance) : null;
            card.Events.Add(NewEvent(BoardEventType.Status, now, before.Status, after.Status, note ?? (isUndo ? "Undo" : null)));
        }

        if (before.IsArchived != after.IsArchived)
        {
            card.ArchivedAtUtc = after.IsArchived ? now : null;
            card.Events.Add(NewEvent(after.IsArchived ? BoardEventType.Archived : BoardEventType.Restored, now, note: isUndo ? "Undo" : null));
        }

        card.Revision++;
        card.ModifiedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Board card {CardId} changed: {Change}", card.Id, isUndo ? "undo" : description);
        return new CardEdit(card.Id, card.Key, description, before, after);
    }

    private static CardState StateOf(BoardCard card) => new(
        card.Title, card.Category, card.Priority, card.Status, card.SortOrder, card.IsArchived, card.Description,
        card.Acceptance, card.Notes, card.Evidence, card.WorkTaskId,
        [.. card.Links.OrderBy(l => l.Position).Select(l => new LinkInput(l.Label, l.Url))],
        [.. card.BlockedBy.Select(d => d.BlockedByCardId).Order()]);

    private static void Apply(WorkTraceDbContext db, BoardCard card, CardState state)
    {
        card.Title = state.Title;
        card.Category = state.Category;
        card.Priority = state.Priority;
        card.Status = state.Status;
        card.SortOrder = state.SortOrder;
        card.IsArchived = state.IsArchived;
        card.Description = state.Description;
        card.Acceptance = state.Acceptance;
        card.Notes = state.Notes;
        card.Evidence = state.Evidence;
        card.WorkTaskId = state.WorkTaskId;
        if (!card.Links.Select(l => new LinkInput(l.Label, l.Url)).SequenceEqual(state.Links))
        {
            ReplaceLinks(db, card, state.Links);
        }

        ReplaceBlockedBy(db, card, state.BlockedByIds);
    }

    private static void ApplyFields(BoardCard card, CardFields fields)
    {
        card.Title = Text.Clean(fields.Title)!;
        card.Category = fields.Category;
        card.Priority = fields.Priority;
        card.Description = CleanText(fields.Description);
        card.Acceptance = BoardExchange.JoinAcceptance(fields.Acceptance?.Split('\n'));
        card.Notes = CleanText(fields.Notes);
        card.Evidence = CleanText(fields.Evidence);
    }

    /// <summary>Normalises line endings and trims trailing whitespace; inner text is kept as typed.</summary>
    private static string CleanText(string? value) => (value ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static void ReplaceLinks(WorkTraceDbContext db, BoardCard card, IReadOnlyList<LinkInput> links)
    {
        db.BoardLinks.RemoveRange(card.Links);
        card.Links.Clear();
        card.Links.AddRange(links.Select((l, i) => new BoardLink { Position = i, Label = l.Label!, Url = l.Url! }));
    }

    private static void ReplaceBlockedBy(WorkTraceDbContext db, BoardCard card, IReadOnlyList<int> blockedByIds)
    {
        var wanted = blockedByIds.Distinct().ToHashSet();
        var removed = card.BlockedBy.Where(d => !wanted.Contains(d.BlockedByCardId)).ToList();
        db.BoardDependencies.RemoveRange(removed);
        card.BlockedBy.RemoveAll(removed.Contains);
        foreach (var id in wanted.Where(w => card.BlockedBy.All(d => d.BlockedByCardId != w)))
        {
            card.BlockedBy.Add(new BoardDependency { BlockedByCardId = id });
        }
    }

    private static async Task ValidateBlockedByAsync(WorkTraceDbContext db, int cardId, IReadOnlyList<int> blockedByIds, CancellationToken cancellationToken)
    {
        if (blockedByIds.Contains(cardId))
        {
            throw DomainValidationException.ForField("BlockedBy", "A card cannot wait for itself.");
        }

        var existing = await db.BoardCards.Where(c => blockedByIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        if (blockedByIds.Except(existing).Any())
        {
            throw DomainValidationException.ForField("BlockedBy", "A chosen card does not exist.");
        }

        var edges = await DependencyEdgesAsync(db, cancellationToken);
        if (BoardRules.CreatesCycle(cardId, blockedByIds, edges))
        {
            throw DomainValidationException.ForField("BlockedBy", "That would create a cycle: a card would end up waiting for itself.");
        }
    }

    private static async Task<Dictionary<int, IReadOnlyList<int>>> DependencyEdgesAsync(WorkTraceDbContext db, CancellationToken cancellationToken) =>
        (await db.BoardDependencies.AsNoTracking().Select(d => new { d.CardId, d.BlockedByCardId }).ToListAsync(cancellationToken))
            .GroupBy(d => d.CardId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(d => d.BlockedByCardId)]);

    // ---- Comments ----

    public async Task<CommentItem> AddCommentAsync(int id, string? text, CancellationToken cancellationToken = default)
    {
        var body = CleanText(text).Trim();
        if (body.Length == 0)
        {
            throw DomainValidationException.ForField("Comment", "Write a comment first.");
        }

        if (body.Length > BoardCard.TextMaxLength)
        {
            throw DomainValidationException.ForField("Comment", $"Use at most {BoardCard.TextMaxLength:N0} characters.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var card = await db.BoardCards.SingleOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new EntityNotFoundException("Card", id);
        var now = UtcTime.Now(timeProvider);
        var comment = new BoardComment { Uid = Guid.NewGuid().ToString(), CardId = id, Author = BoardRules.OwnerAuthor, CreatedAtUtc = now, Text = body };
        db.BoardComments.Add(comment);
        card.Revision++;
        card.ModifiedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return new CommentItem(comment.Id, comment.Author, comment.CreatedAtUtc, comment.Text);
    }

    private static BoardCardEvent NewEvent(BoardEventType type, DateTime at, BoardStatus? from = null, BoardStatus? to = null, string? note = null) =>
        new() { Uid = Guid.NewGuid().ToString(), Type = type, AtUtc = at, By = BoardRules.OwnerAuthor, From = from, To = to, Note = note };
}
