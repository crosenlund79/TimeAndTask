using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Board;

// Attachments (files under the data directory) and import of web-board data.
internal sealed partial class BoardService
{
    // ---- Attachments ----

    public async Task<AttachmentItem> AddAttachmentAsync(
        int cardId, string originalName, string contentType, byte[] bytes, string? caption = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0)
        {
            throw new OperationNotAllowedException("The file is empty.");
        }

        if (bytes.Length > BoardExchange.MaxAttachmentBytes)
        {
            throw new OperationNotAllowedException($"The file is larger than {BoardExchange.MaxAttachmentBytes / (1024 * 1024)} MB.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var card = await db.BoardCards.SingleOrDefaultAsync(c => c.Id == cardId, cancellationToken) ?? throw new EntityNotFoundException("Card", cardId);
        var now = UtcTime.Now(timeProvider);
        var attachment = await WriteAttachmentAsync(Guid.NewGuid().ToString(), originalName, contentType, bytes, caption, now, cancellationToken);
        attachment.CardId = cardId;
        db.BoardAttachments.Add(attachment);
        card.Revision++;
        card.ModifiedAtUtc = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            DeleteFile(attachment.RelativePath);
            throw;
        }

        logger.LogInformation("Attachment {AttachmentId} ({Bytes} bytes) added to board card {CardId}", attachment.Id, bytes.Length, cardId);
        return ToItem(attachment);
    }

    public async Task<AttachmentItem> SetAttachmentCaptionAsync(int attachmentId, string? caption, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.BoardAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new EntityNotFoundException("Attachment", attachmentId);
        attachment.Caption = CleanCaption(caption);
        await db.SaveChangesAsync(cancellationToken);
        return ToItem(attachment);
    }

    public async Task RemoveAttachmentAsync(int attachmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var attachment = await db.BoardAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, cancellationToken)
            ?? throw new EntityNotFoundException("Attachment", attachmentId);
        var card = await db.BoardCards.SingleAsync(c => c.Id == attachment.CardId, cancellationToken);
        db.BoardAttachments.Remove(attachment);
        card.Revision++;
        card.ModifiedAtUtc = UtcTime.Now(timeProvider);
        await db.SaveChangesAsync(cancellationToken);
        DeleteFile(attachment.RelativePath);
        logger.LogInformation("Attachment {AttachmentId} removed from board card {CardId}", attachmentId, card.Id);
    }

    private AttachmentItem ToItem(BoardAttachment a) => new(
        a.Id, a.Uid, a.CardId, a.OriginalName, a.Caption, a.ContentType, a.SizeBytes, FullPath(a.RelativePath), a.CreatedAtUtc);

    private string FullPath(string relativePath) =>
        Path.Combine(paths.AttachmentsDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string CleanCaption(string? caption)
    {
        var text = Text.Clean(caption) ?? "";
        return text.Length > BoardAttachment.CaptionMaxLength ? text[..BoardAttachment.CaptionMaxLength] : text;
    }

    /// <summary>Writes the bytes to <c>Attachments/yyyy/MM/{uid}.ext</c> (atomically) and returns the unsaved row.</summary>
    private async Task<BoardAttachment> WriteAttachmentAsync(
        string uid, string originalName, string contentType, byte[] bytes, string? caption, DateTime createdAtUtc, CancellationToken cancellationToken)
    {
        var name = Text.Clean(Path.GetFileName(originalName ?? "")) ?? "image.png";
        if (name.Length > BoardAttachment.NameMaxLength)
        {
            name = name[^BoardAttachment.NameMaxLength..];
        }

        var type = Text.Clean(contentType) ?? ContentTypeFor(name);
        var fileStem = SafeStem().IsMatch(uid) ? uid : Guid.NewGuid().ToString();
        var relative = $"{createdAtUtc:yyyy}/{createdAtUtc:MM}/{fileStem}{ExtensionFor(name, type)}";
        var full = FullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp";
        await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
        File.Move(temp, full, overwrite: true);

        return new BoardAttachment
        {
            Uid = uid,
            RelativePath = relative,
            OriginalName = name,
            Caption = CleanCaption(caption),
            ContentType = type.Length > 100 ? "application/octet-stream" : type,
            SizeBytes = bytes.Length,
            Sha256 = BoardExchange.Sha256(bytes),
            CreatedAtUtc = createdAtUtc,
        };
    }

    private void DeleteFile(string relativePath)
    {
        try
        {
            File.Delete(FullPath(relativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Attachment file could not be deleted");
        }
    }

    private static string ExtensionFor(string name, string contentType)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        if (ext.Length is > 1 and <= 10 && ext[1..].All(char.IsAsciiLetterOrDigit))
        {
            return ext;
        }

        return contentType switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            _ => ".bin",
        };
    }

    internal static string ContentTypeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain",
        _ => "application/octet-stream",
    };

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex SafeStem();

    // ---- Import ----

    public async Task<ImportPreview> PreviewImportAsync(string path, CancellationToken cancellationToken = default)
    {
        var package = await BoardExchange.ReadAsync(path, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var plan = await PlanAsync(db, package, cancellationToken);
        return new ImportPreview(package.Source, [.. plan.Select(p => p.Item)], package.Problems, package.Attachments.Count);
    }

    public async Task<ImportResult> ImportAsync(string path, IReadOnlyCollection<string> overwriteUids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(overwriteUids);
        var package = await BoardExchange.ReadAsync(path, cancellationToken);
        var written = new List<string>();
        try
        {
            return await ImportCoreAsync(package, overwriteUids, written, cancellationToken);
        }
        catch
        {
            written.ForEach(DeleteFile);
            throw;
        }
    }

    private sealed record PlannedCard(WebCard Web, ImportItem Item, BoardCard? Local, int Number);

    private static async Task<List<PlannedCard>> PlanAsync(WorkTraceDbContext db, BoardPackage package, CancellationToken cancellationToken)
    {
        var locals = await db.BoardCards.AsSplitQuery()
            .Include(c => c.Links).Include(c => c.BlockedBy).Include(c => c.Comments).Include(c => c.Events).Include(c => c.Attachments)
            .ToListAsync(cancellationToken);
        var byUid = locals.ToDictionary(c => c.Uid, StringComparer.Ordinal);
        var numberById = locals.ToDictionary(c => c.Id, c => c.Number);
        var takenNumbers = locals.Select(c => c.Number).ToHashSet();
        var state = await db.BoardState.SingleAsync(cancellationToken);
        var next = Math.Max(state.NextNumber, Math.Max(package.NextId, takenNumbers.DefaultIfEmpty(0).Max() + 1));

        // Numbers the package keeps (its own cards that are new here and whose numbers are free) are reserved first.
        var plan = new List<PlannedCard>();
        foreach (var web in package.Cards.OrderBy(c => BoardRules.ParseNumber(c.Id)))
        {
            var number = BoardRules.ParseNumber(web.Id)!.Value;
            var newAttachments = package.Attachments.Count(a => a.CardUid == web.Uid
                && (!byUid.TryGetValue(web.Uid, out var l) || l.Attachments.All(x => x.Uid != a.Id)));
            if (byUid.TryGetValue(web.Uid, out var local))
            {
                var newComments = web.Comments.Count(c => local.Comments.All(x => x.Uid != c.Id));
                var newEvents = web.History.Count(h => local.Events.All(x => x.Uid != h.Id));
                var same = BoardExchange.SameContent(BoardExchange.ToWeb(local, numberById, web.Rank), web);
                var kind = same
                    ? (newComments + newEvents + newAttachments == 0 ? ImportItemKind.Same : ImportItemKind.Changed)
                    : local.Revision > web.Rev || local.ModifiedAtUtc > BoardExchange.ParseTimestamp(web.UpdatedAt)
                        ? ImportItemKind.LocalNewer
                        : ImportItemKind.Changed;
                plan.Add(new PlannedCard(web, new ImportItem(web.Uid, web.Id, web.Title.Trim(), kind, local.Key == web.Id ? null : local.Key, newComments, newAttachments), local, local.Number));
            }
            else if (takenNumbers.Add(number))
            {
                plan.Add(new PlannedCard(web, new ImportItem(web.Uid, web.Id, web.Title.Trim(), ImportItemKind.New, null, web.Comments.Count, newAttachments), null, number));
            }
            else
            {
                plan.Add(new PlannedCard(web, null!, null, 0));
            }
        }

        // Cards whose number is taken by a different card get fresh numbers.
        for (var i = 0; i < plan.Count; i++)
        {
            if (plan[i].Item is null)
            {
                while (!takenNumbers.Add(next))
                {
                    next++;
                }

                var web = plan[i].Web;
                plan[i] = plan[i] with
                {
                    Number = next,
                    Item = new ImportItem(web.Uid, web.Id, web.Title.Trim(), ImportItemKind.NumberTaken, BoardCard.FormatKey(next), web.Comments.Count,
                        package.Attachments.Count(a => a.CardUid == web.Uid)),
                };
            }
        }

        return plan;
    }

    private async Task<ImportResult> ImportCoreAsync(BoardPackage package, IReadOnlyCollection<string> overwriteUids, List<string> written, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var plan = await PlanAsync(db, package, cancellationToken);
        var now = UtcTime.Now(timeProvider);
        var notes = new List<string>();
        int added = 0, updated = 0, unchanged = 0, skipped = 0, attachmentsAdded = 0;

        // Sort values: new and moved cards go below the cards already in a column, in the package's rank order.
        var columnEnds = (await db.BoardCards.Where(c => !c.IsArchived).GroupBy(c => c.Status)
                .Select(g => new { g.Key, Max = g.Max(c => c.SortOrder) }).ToListAsync(cancellationToken))
            .ToDictionary(g => g.Key, g => g.Max);
        double NextSort(BoardStatus status)
        {
            var value = (columnEnds.TryGetValue(status, out var end) ? end : 0) + BoardRules.SortStep;
            columnEnds[status] = value;
            return value;
        }

        var cardByKey = new Dictionary<string, BoardCard>(StringComparer.Ordinal);
        var applyFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var planned in plan.OrderBy(p => BoardExchange.ParseStatus(p.Web.Status)).ThenBy(p => p.Web.Rank, StringComparer.Ordinal))
        {
            var web = planned.Web;
            var status = BoardExchange.ParseStatus(web.Status)!.Value;
            var card = planned.Local;
            var write = planned.Item.Kind is ImportItemKind.New or ImportItemKind.NumberTaken or ImportItemKind.Changed
                || (planned.Item.Kind == ImportItemKind.LocalNewer && overwriteUids.Contains(web.Uid));

            if (card is null)
            {
                card = new BoardCard
                {
                    Number = planned.Number,
                    Uid = web.Uid,
                    Title = web.Title.Trim(),
                    CreatedAtUtc = BoardExchange.ParseTimestamp(web.CreatedAt)!.Value,
                };
                db.BoardCards.Add(card);
                added++;
            }
            else if (write && planned.Item.Kind != ImportItemKind.Same)
            {
                updated++;
            }
            else
            {
                if (planned.Item.Kind == ImportItemKind.LocalNewer)
                {
                    skipped++;
                }
                else
                {
                    unchanged++;
                }
            }

            cardByKey[web.Id] = card;
            if (write)
            {
                applyFields.Add(web.Uid);
                var sameColumn = card.Id != 0 && card.Status == status && card.IsArchived == web.Archived;
                card.Title = web.Title.Trim();
                card.Category = BoardExchange.ParseCategory(web.Category);
                card.Priority = BoardExchange.ParsePriority(web.Priority)!.Value;
                card.Status = status;
                card.IsArchived = web.Archived;
                card.ArchivedAtUtc = web.Archived ? BoardExchange.ParseTimestamp(web.ArchivedAt) ?? now : null;
                card.Description = web.Description;
                card.Acceptance = BoardExchange.JoinAcceptance(web.Acceptance);
                card.Notes = web.Notes;
                card.Evidence = web.Evidence;
                card.Revision = Math.Max(card.Revision, web.Rev);
                card.ModifiedAtUtc = card.Id == 0 ? BoardExchange.ParseTimestamp(web.UpdatedAt)!.Value : now;
                if (!sameColumn)
                {
                    card.SortOrder = NextSort(status);
                }

                ReplaceLinks(db, card, [.. web.Links.Select(l => new LinkInput(Text.Clean(l.Label) ?? BoardRules.DefaultLabel(l.Url), l.Url))]);
            }

            // Comments and history are append-only: merged by id whether or not the fields are overwritten.
            foreach (var comment in web.Comments.Where(c => card.Comments.All(x => x.Uid != c.Id) && !string.IsNullOrWhiteSpace(c.Text)))
            {
                card.Comments.Add(new BoardComment
                {
                    Uid = comment.Id,
                    Author = Truncate(Text.Clean(comment.Author) ?? "unknown", BoardComment.AuthorMaxLength),
                    CreatedAtUtc = BoardExchange.ParseTimestamp(comment.At)!.Value,
                    Text = comment.Text,
                });
            }

            foreach (var history in web.History.Where(h => card.Events.All(x => x.Uid != h.Id)))
            {
                card.Events.Add(new BoardCardEvent
                {
                    Uid = history.Id,
                    Type = BoardExchange.ParseEventType(history.Type) ?? BoardEventType.Imported,
                    AtUtc = BoardExchange.ParseTimestamp(history.At)!.Value,
                    By = Truncate(Text.Clean(history.By) ?? "unknown", BoardComment.AuthorMaxLength),
                    From = BoardExchange.ParseStatus(history.From),
                    To = BoardExchange.ParseStatus(history.To),
                    Note = Text.Clean(history.Accepted ?? history.Note),
                });
            }

            if (write && planned.Item.Kind != ImportItemKind.Same)
            {
                var note = planned.Item.Kind == ImportItemKind.NumberTaken
                    ? $"Imported from {package.Source} (was {web.Id})"
                    : $"Imported from {package.Source}";
                card.Events.Add(NewEvent(BoardEventType.Imported, now, note: Truncate(note, 2000)));
            }

            foreach (var file in package.Attachments.Where(a => a.CardUid == web.Uid && card.Attachments.All(x => x.Uid != a.Id)))
            {
                var uid = Text.Clean(file.Id) is { Length: <= BoardCard.UidMaxLength } id ? id : Guid.NewGuid().ToString();
                if (await db.BoardAttachments.AnyAsync(a => a.Uid == uid, cancellationToken))
                {
                    continue; // attached to another card already
                }

                var attachment = await WriteAttachmentAsync(uid, file.Name, file.ContentType, file.Bytes, file.Caption, file.CreatedAtUtc ?? now, cancellationToken);
                written.Add(attachment.RelativePath);
                card.Attachments.Add(attachment);
                attachmentsAdded++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Dependencies after every card has an id. Keys resolve to the imported cards first, then to cards already here.
        var localByNumber = await db.BoardCards.ToDictionaryAsync(c => c.Number, cancellationToken);
        var edges = await DependencyEdgesAsync(db, cancellationToken);
        foreach (var planned in plan.Where(p => applyFields.Contains(p.Web.Uid)))
        {
            var card = cardByKey[planned.Web.Id];
            var wanted = new List<int>();
            edges[card.Id] = [];
            foreach (var key in planned.Web.BlockedBy)
            {
                var target = cardByKey.TryGetValue(key, out var imported) ? imported
                    : BoardRules.ParseNumber(key) is { } n && localByNumber.TryGetValue(n, out var local) ? local : null;
                if (target is null || target.Id == card.Id)
                {
                    notes.Add($"{card.Key}: dependency on {key} dropped (not on this board).");
                    continue;
                }

                if (BoardRules.CreatesCycle(card.Id, [target.Id], edges))
                {
                    notes.Add($"{card.Key}: dependency on {target.Key} dropped (it would create a cycle).");
                    continue;
                }

                wanted.Add(target.Id);
                edges[card.Id] = [.. (edges.TryGetValue(card.Id, out var e) ? e : []).Append(target.Id)];
            }

            ReplaceBlockedBy(db, card, wanted);
        }

        var state = await db.BoardState.SingleAsync(cancellationToken);
        state.NextNumber = Math.Max(state.NextNumber, Math.Max(package.NextId, localByNumber.Keys.DefaultIfEmpty(0).Max() + 1));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        notes.AddRange(package.Problems);
        logger.LogInformation(
            "Board import: {Added} added, {Updated} updated, {Unchanged} unchanged, {Skipped} kept, {Attachments} attachments",
            added, updated, unchanged, skipped, attachmentsAdded);
        return new ImportResult(added, updated, unchanged, skipped, attachmentsAdded, notes);
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
