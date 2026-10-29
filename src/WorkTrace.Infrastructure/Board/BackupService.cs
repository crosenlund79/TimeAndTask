using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Board;

/// <summary>
/// Writes backup ZIPs in the web board's format (README.txt, manifest.json, cards.json, cards.md,
/// attachments/…). A complete backup adds database/worktrace.db (a consistent snapshot) and settings/settings.json.
/// </summary>
internal sealed class BackupService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    AppPaths paths,
    TimeProvider timeProvider,
    ILogger<BackupService> logger) : IBackupService
{
    private const string MonthScopePrefix = "month:";

    private sealed record Snapshot(List<WebCard> Cards, List<(BoardAttachment Row, WebCard Card)> Attachments, int NextId);

    public async Task<IReadOnlyList<BackupMonth>> GetMonthsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await LoadAsync(db, cancellationToken);
        var records = await db.BackupRecords.AsNoTracking().Where(r => r.Scope.StartsWith(MonthScopePrefix))
            .ToListAsync(cancellationToken);

        return [.. snapshot.Cards.GroupBy(c => c.CreatedAt[..7]).OrderByDescending(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var attachments = snapshot.Attachments.Where(a => g.Contains(a.Card)).ToList();
            var hash = ContentHash(g, attachments.Select(a => a.Row.Sha256));
            var last = records.Where(r => r.Scope == MonthScopePrefix + g.Key).OrderByDescending(r => r.CreatedAtUtc).FirstOrDefault();
            return new BackupMonth(g.Key, g.Count(), attachments.Count, hash, last?.CreatedAtUtc, last is null || last.ContentHash != hash);
        })];
    }

    public string DefaultFileName(BackupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var day = UtcTime.Now(timeProvider).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return request.Kind switch
        {
            BackupKind.Complete => $"worktrace-complete-{day}.zip",
            BackupKind.Month => $"worktrace-backup-{request.Month}.zip",
            BackupKind.Cards => $"worktrace-backup-{request.CardIds?.Count ?? 0}-cards-{day}.zip",
            _ => $"worktrace-backup-all-{day}.zip",
        };
    }

    public async Task<BackupResult> CreateAsync(BackupRequest request, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var snapshot = await LoadAsync(db, cancellationToken);
        var now = UtcTime.Now(timeProvider);

        var cards = request.Kind switch
        {
            BackupKind.Month => snapshot.Cards.Where(c => c.CreatedAt.StartsWith(request.Month ?? "-", StringComparison.Ordinal)).ToList(),
            _ => snapshot.Cards,
        };
        if (request.Kind == BackupKind.Cards)
        {
            var ids = request.CardIds ?? [];
            var numbers = await db.BoardCards.Where(c => ids.Contains(c.Id)).Select(c => c.Number).ToListAsync(cancellationToken);
            cards = [.. snapshot.Cards.Where(c => numbers.Contains(BoardRules.ParseNumber(c.Id)!.Value))];
        }

        if (cards.Count == 0 && request.Kind != BackupKind.Complete)
        {
            throw new OperationNotAllowedException("There are no cards to back up for this choice.");
        }

        var selected = cards.ToHashSet();
        var attachments = snapshot.Attachments.Where(a => selected.Contains(a.Card)).ToList();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        var databaseSnapshot = request.Kind == BackupKind.Complete ? Path.Combine(paths.BackupsDirectory, $"snapshot-{Guid.NewGuid():N}.db") : null;
        try
        {
            object? database = null;
            if (databaseSnapshot is not null)
            {
                Directory.CreateDirectory(paths.BackupsDirectory);
                SnapshotDatabase(db, databaseSnapshot);
                var bytes = await File.ReadAllBytesAsync(databaseSnapshot, cancellationToken);
                database = new { path = "database/worktrace.db", bytes = bytes.Length, sha256 = BoardExchange.Sha256(bytes) };
            }

            var attachmentEntries = attachments.Select(a => new
            {
                id = a.Row.Uid,
                cardId = a.Card.Id,
                cardUid = a.Card.Uid,
                path = $"attachments/{a.Card.Id}/{a.Row.Uid}-{SafeName(a.Row.OriginalName)}",
                name = a.Row.OriginalName,
                type = a.Row.ContentType,
                bytes = a.Row.SizeBytes,
                caption = a.Row.Caption,
                createdAt = BoardExchange.Timestamp(a.Row.CreatedAtUtc),
                sha256 = a.Row.Sha256,
                file = Path.Combine(paths.AttachmentsDirectory, a.Row.RelativePath.Replace('/', Path.DirectorySeparatorChar)),
            }).ToList();

            var manifest = new
            {
                format = BoardExchange.BackupFormat,
                formatVersion = BoardExchange.BackupVersion,
                createdAt = BoardExchange.Timestamp(now),
                createdBy = "WorkTrace",
                scope = request.Kind switch
                {
                    BackupKind.Month => (object)new { kind = "month", month = request.Month },
                    BackupKind.Cards => new { kind = "cards", ids = cards.Select(c => c.Id).ToList() },
                    _ => new { kind = "all" },
                },
                complete = request.Kind == BackupKind.Complete,
                board = new { schemaVersion = BoardExchange.SchemaVersion, nextId = snapshot.NextId },
                cards = cards.Select(c => new { id = c.Id, uid = c.Uid, title = c.Title, rev = c.Rev, createdAt = c.CreatedAt, updatedAt = c.UpdatedAt, archived = c.Archived }),
                attachments = attachmentEntries.Select(a => new { a.id, a.cardId, a.cardUid, a.path, a.name, a.type, a.bytes, a.caption, a.createdAt, a.sha256 }),
                database,
                contentHash = ContentHash(cards, attachments.Select(a => a.Row.Sha256)),
            };

            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await AddTextAsync(zip, "README.txt", Readme(request, cards.Count, attachments.Count, now), cancellationToken);
                await AddTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, BoardExchange.Json) + "\n", cancellationToken);
                await AddTextAsync(zip, "cards.json",
                    JsonSerializer.Serialize(new WebBoard { SchemaVersion = BoardExchange.SchemaVersion, NextId = snapshot.NextId, Cards = cards }, BoardExchange.Json) + "\n",
                    cancellationToken);
                await AddTextAsync(zip, "cards.md", Markdown(cards), cancellationToken);
                foreach (var a in attachmentEntries)
                {
                    if (!File.Exists(a.file))
                    {
                        throw new OperationNotAllowedException($"The screenshot {a.name} of {a.cardId} is missing from the data folder; the backup was not written.");
                    }

                    await AddFileAsync(zip, a.path, a.file, cancellationToken);
                }

                if (databaseSnapshot is not null)
                {
                    await AddFileAsync(zip, "database/worktrace.db", databaseSnapshot, cancellationToken);
                    if (File.Exists(paths.SettingsPath))
                    {
                        await AddFileAsync(zip, "settings/settings.json", paths.SettingsPath, cancellationToken);
                    }
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
            if (databaseSnapshot is not null)
            {
                TryDelete(databaseSnapshot);
            }
        }

        var size = new FileInfo(path).Length;
        await RecordAsync(db, request, cards, attachments, Path.GetFileName(path), size, now, cancellationToken);
        logger.LogInformation("Backup written: {Kind}, {Cards} cards, {Attachments} attachments, {Bytes} bytes", request.Kind, cards.Count, attachments.Count, size);
        return new BackupResult(path, cards.Count, attachments.Count, size, databaseSnapshot is not null);
    }

    /// <summary>Records the per-month content hash of every month the backup fully covers.</summary>
    private static async Task RecordAsync(
        WorkTraceDbContext db, BackupRequest request, List<WebCard> cards, List<(BoardAttachment Row, WebCard Card)> attachments,
        string fileName, long size, DateTime now, CancellationToken cancellationToken)
    {
        if (request.Kind == BackupKind.Cards)
        {
            return;
        }

        foreach (var month in cards.GroupBy(c => c.CreatedAt[..7]))
        {
            db.BackupRecords.Add(new BackupRecord
            {
                Scope = MonthScopePrefix + month.Key,
                ContentHash = ContentHash(month, attachments.Where(a => month.Contains(a.Card)).Select(a => a.Row.Sha256)),
                FileName = fileName.Length > BackupRecord.FileNameMaxLength ? fileName[..BackupRecord.FileNameMaxLength] : fileName,
                SizeBytes = size,
                CreatedAtUtc = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>All cards in the web format (ranks from the current order) and their attachment rows.</summary>
    private static async Task<Snapshot> LoadAsync(WorkTraceDbContext db, CancellationToken cancellationToken)
    {
        var cards = await db.BoardCards.AsNoTracking().AsSplitQuery()
            .Include(c => c.Links).Include(c => c.BlockedBy).Include(c => c.Comments).Include(c => c.Events).Include(c => c.Attachments)
            .ToListAsync(cancellationToken);
        var numberById = cards.ToDictionary(c => c.Id, c => c.Number);
        var ranks = cards.GroupBy(c => c.Status)
            .SelectMany(g => g.OrderBy(c => c.IsArchived).ThenBy(c => c.SortOrder).ThenBy(c => c.Number).Select((c, i) => (c.Id, Rank: BoardRules.RankFor(i))))
            .ToDictionary(x => x.Id, x => x.Rank);

        var web = new List<WebCard>();
        var attachments = new List<(BoardAttachment, WebCard)>();
        foreach (var card in cards.OrderBy(c => c.Number))
        {
            var w = BoardExchange.ToWeb(card, numberById, ranks[card.Id]);
            web.Add(w);
            attachments.AddRange(card.Attachments.OrderBy(a => a.CreatedAtUtc).ThenBy(a => a.Id).Select(a => (a, w)));
        }

        var state = await db.BoardState.AsNoTracking().SingleAsync(cancellationToken);
        var nextId = Math.Max(state.NextNumber, cards.Select(c => c.Number).DefaultIfEmpty(0).Max() + 1);
        return new Snapshot(web, attachments, nextId);
    }

    /// <summary>Hash of everything a backup of these cards contains; unchanged hash = nothing new to back up.</summary>
    internal static string ContentHash(IEnumerable<WebCard> cards, IEnumerable<string> attachmentHashes)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            cards = cards.OrderBy(c => BoardRules.ParseNumber(c.Id)),
            attachments = attachmentHashes.Order(StringComparer.Ordinal),
        }, BoardExchange.Json);
        return BoardExchange.Sha256(Encoding.UTF8.GetBytes(canonical));
    }

    /// <summary>Consistent copy of the live database (WAL included) via SQLite's online backup.</summary>
    private static void SnapshotDatabase(WorkTraceDbContext db, string target)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        var wasOpen = connection.State == System.Data.ConnectionState.Open;
        if (!wasOpen)
        {
            connection.Open();
        }

        try
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = target, Pooling = false }.ToString());
            connection.BackupDatabase(destination);
        }
        finally
        {
            if (!wasOpen)
            {
                connection.Close();
            }
        }
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken cancellationToken)
    {
        await using var entry = await zip.CreateEntry(name, CompressionLevel.Optimal).OpenAsync(cancellationToken);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        await entry.WriteAsync(bytes, cancellationToken);
    }

    private static async Task AddFileAsync(ZipArchive zip, string name, string file, CancellationToken cancellationToken)
    {
        var level = Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
        await using var entry = await zip.CreateEntry(name, level).OpenAsync(cancellationToken);
        await using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await source.CopyToAsync(entry, cancellationToken);
    }

    private static string SafeName(string name)
    {
        var safe = new string([.. name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]);
        return safe.Length > 60 ? safe[..60] : safe.Length == 0 ? "file" : safe;
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Temporary backup file could not be deleted");
        }
    }

    private static string Readme(BackupRequest request, int cardCount, int imageCount, DateTime now)
    {
        var scope = request.Kind switch
        {
            BackupKind.Complete => "everything: all cards, screenshots, the WorkTrace database (time tracking) and settings",
            BackupKind.Month => $"cards created in {request.Month} (UTC)",
            BackupKind.Cards => $"{cardCount} selected cards",
            _ => "all cards",
        };
        return string.Join("\r\n",
            "WorkTrace backup",
            "================",
            "",
            $"Contains {scope}: {cardCount} card(s) and {imageCount} screenshot(s), as of {BoardExchange.Timestamp(now)}.",
            "Every card is complete: description, acceptance criteria, notes, evidence, links, comments, history,",
            "archived state and screenshots.",
            "",
            "Restore cards: in WorkTrace open Board > Backup > Restore and choose this ZIP. WorkTrace shows what would",
            "change (new, changed, unchanged) before anything is restored. Restoring never deletes cards or comments.",
            request.Kind == BackupKind.Complete
                ? "The full database snapshot (database/worktrace.db) is restored manually; see docs/backup.md."
                : "",
            "",
            "Files: cards.json (data), cards.md (readable), attachments/ (screenshots), manifest.json (checksums).",
            "");
    }

    private static string Markdown(List<WebCard> cards)
    {
        var sb = new StringBuilder();
        sb.Append("# WorkTrace board backup\n\n");
        foreach (var c in cards)
        {
            sb.Append(CultureInfo.InvariantCulture, $"## {c.Id} {c.Title}\n\n");
            sb.Append(CultureInfo.InvariantCulture, $"Status: {c.Status}{(c.Archived ? " (archived)" : "")} · Category: {c.Category ?? "none"} · Priority: {c.Priority} · Created {c.CreatedAt}\n\n");
            void Section(string title, string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"**{title}**\n\n{text.Trim()}\n\n");
                }
            }

            Section("Description", c.Description);
            Section("Acceptance criteria", string.Join("\n", c.Acceptance.Select(a => "- " + a)));
            Section("Notes", c.Notes);
            Section("Evidence", c.Evidence);
            Section("Blocked by", string.Join(", ", c.BlockedBy));
            Section("Links", string.Join("\n", c.Links.Select(l => $"- [{l.Label}]({l.Url})")));
            Section("Comments", string.Join("\n", c.Comments.Select(m => $"- {m.At} {m.Author}: {m.Text}")));
        }

        return sb.ToString();
    }
}
