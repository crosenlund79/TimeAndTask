using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;

namespace WorkTrace.Infrastructure.Board;

// The web board's data format (docs/board.json on the `board` branch, schema version 1-2) and its backup
// ZIP ("worktrace-board-backup" v1). WorkTrace reads both and writes the same backup format, so archives
// move freely between the old browser board and the Windows app.

internal sealed class WebBoard
{
    public int SchemaVersion { get; set; } = BoardExchange.SchemaVersion;

    public int NextId { get; set; }

    public List<WebCard> Cards { get; set; } = [];
}

internal sealed class WebCard
{
    public string Id { get; set; } = "";

    public string Uid { get; set; } = "";

    public string Title { get; set; } = "";

    public string? Category { get; set; }

    public string Priority { get; set; } = "normal";

    public string Status { get; set; } = "backlog";

    public string Rank { get; set; } = "i";

    public bool Archived { get; set; }

    public string? ArchivedAt { get; set; }

    public string Description { get; set; } = "";

    public List<string> Acceptance { get; set; } = [];

    public string Notes { get; set; } = "";

    public string Evidence { get; set; } = "";

    public List<string> BlockedBy { get; set; } = [];

    public List<WebLink> Links { get; set; } = [];

    public List<WebComment> Comments { get; set; } = [];

    public List<WebEvent> History { get; set; } = [];

    public string CreatedAt { get; set; } = "";

    public string UpdatedAt { get; set; } = "";

    public int Rev { get; set; } = 1;
}

internal sealed class WebLink
{
    public string Label { get; set; } = "";

    public string Url { get; set; } = "";
}

internal sealed class WebComment
{
    public string Id { get; set; } = "";

    public string Author { get; set; } = "";

    public string At { get; set; } = "";

    public string Text { get; set; } = "";
}

internal sealed class WebEvent
{
    public string Id { get; set; } = "";

    public string Type { get; set; } = "";

    public string At { get; set; } = "";

    public string By { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Accepted { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; set; }
}

internal sealed record PackageAttachment(
    string Id, string CardUid, string Name, string ContentType, string Caption, DateTime? CreatedAtUtc, byte[] Bytes, string Sha256);

/// <summary>Cards (and attachments) read from a board.json or a backup ZIP, validated.</summary>
internal sealed record BoardPackage(
    string Source, int NextId, IReadOnlyList<WebCard> Cards, IReadOnlyList<PackageAttachment> Attachments, IReadOnlyList<string> Problems);

internal static class BoardExchange
{
    public const int SchemaVersion = 2;
    public const string BackupFormat = "worktrace-board-backup";
    public const int BackupVersion = 1;
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ---- Enum names ----

    public static string StatusName(BoardStatus status) => status.ToString().ToLowerInvariant();

    public static BoardStatus? ParseStatus(string? value) => value switch
    {
        "inbox" => BoardStatus.Inbox,
        "backlog" => BoardStatus.Backlog,
        "todo" => BoardStatus.Todo,
        "doing" => BoardStatus.Doing,
        "review" => BoardStatus.Review,
        "done" => BoardStatus.Done,
        _ => null,
    };

    public static string? CategoryName(BoardCategory? category) => category?.ToString().ToLowerInvariant();

    public static BoardCategory? ParseCategory(string? value) =>
        Enum.TryParse<BoardCategory>(value, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : null;

    public static string PriorityName(BoardPriority priority) => priority.ToString().ToLowerInvariant();

    public static BoardPriority? ParsePriority(string? value) =>
        Enum.TryParse<BoardPriority>(value, ignoreCase: true, out var p) && Enum.IsDefined(p) ? p : null;

    public static BoardEventType? ParseEventType(string? value) =>
        Enum.TryParse<BoardEventType>(value, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : null;

    public static string Timestamp(DateTime utc) => utc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTime? ParseTimestamp(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
            : null;

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    // ---- Local card → web card ----

    public static WebCard ToWeb(BoardCard card, IReadOnlyDictionary<int, int> numberById, string rank) => new()
    {
        Id = card.Key,
        Uid = card.Uid,
        Title = card.Title,
        Category = CategoryName(card.Category),
        Priority = PriorityName(card.Priority),
        Status = StatusName(card.Status),
        Rank = rank,
        Archived = card.IsArchived,
        ArchivedAt = card.ArchivedAtUtc is { } a ? Timestamp(a) : null,
        Description = card.Description,
        Acceptance = SplitAcceptance(card.Acceptance),
        Notes = card.Notes,
        Evidence = card.Evidence,
        BlockedBy = [.. card.BlockedBy.Select(d => numberById.TryGetValue(d.BlockedByCardId, out var n) ? BoardCard.FormatKey(n) : null)
            .OfType<string>().Order(StringComparer.Ordinal)],
        Links = [.. card.Links.OrderBy(l => l.Position).Select(l => new WebLink { Label = l.Label, Url = l.Url })],
        Comments = [.. card.Comments.OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
            .Select(c => new WebComment { Id = c.Uid, Author = c.Author, At = Timestamp(c.CreatedAtUtc), Text = c.Text })],
        History = [.. card.Events.OrderBy(e => e.AtUtc).ThenBy(e => e.Id).Select(e => new WebEvent
        {
            Id = e.Uid,
            Type = e.Type.ToString().ToLowerInvariant(),
            At = Timestamp(e.AtUtc),
            By = e.By,
            From = e.From is { } f ? StatusName(f) : null,
            To = e.To is { } t ? StatusName(t) : null,
            Accepted = e.Type == BoardEventType.Status && e.To == BoardStatus.Done ? e.Note : null,
            Note = e.Type == BoardEventType.Status && e.To == BoardStatus.Done ? null : e.Note,
        })],
        CreatedAt = Timestamp(card.CreatedAtUtc),
        UpdatedAt = Timestamp(card.ModifiedAtUtc),
        Rev = card.Revision,
    };

    public static List<string> SplitAcceptance(string acceptance) =>
        [.. acceptance.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    public static string JoinAcceptance(IEnumerable<string>? lines) =>
        string.Join("\n", (lines ?? []).Select(l => l.Trim()).Where(l => l.Length > 0));

    /// <summary>True when the user-visible content (not comments, history or attachments) is the same.</summary>
    public static bool SameContent(WebCard a, WebCard b) =>
        a.Title == b.Title && a.Category == b.Category && a.Priority == b.Priority && a.Status == b.Status
        && a.Archived == b.Archived && a.Description == b.Description && a.Notes == b.Notes && a.Evidence == b.Evidence
        && a.Acceptance.SequenceEqual(b.Acceptance)
        && a.BlockedBy.Order(StringComparer.Ordinal).SequenceEqual(b.BlockedBy.Order(StringComparer.Ordinal))
        && a.Links.Select(l => (l.Label, l.Url)).SequenceEqual(b.Links.Select(l => (l.Label, l.Url)));

    // ---- Reading ----

    /// <summary>Reads a board.json or a backup ZIP.</summary>
    /// <exception cref="OperationNotAllowedException">The file is not a usable board file.</exception>
    public static async Task<BoardPackage> ReadAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new OperationNotAllowedException($"The file could not be read: {ex.Message}");
        }

        var name = Path.GetFileName(path);
        return bytes.Length >= 2 && bytes[0] == 'P' && bytes[1] == 'K' ? ReadZip(name, bytes) : ReadBoardJson(name, bytes, []);
    }

    private static BoardPackage ReadBoardJson(string source, byte[] bytes, List<string> problems, IReadOnlyList<PackageAttachment>? attachments = null)
    {
        WebBoard? board;
        try
        {
            board = JsonSerializer.Deserialize<WebBoard>(bytes, Json);
        }
        catch (JsonException)
        {
            throw new OperationNotAllowedException($"{source} is not a WorkTrace board file (invalid JSON).");
        }

        if (board?.Cards is null || board.SchemaVersion is < 1 or > SchemaVersion)
        {
            throw new OperationNotAllowedException(board is null || board.Cards is null
                ? $"{source} is not a WorkTrace board file."
                : $"{source} uses an unknown board format (schema {board.SchemaVersion}).");
        }

        var errors = Validate(board.Cards);
        if (errors.Count > 0)
        {
            throw new OperationNotAllowedException($"The cards in {source} are not valid: {string.Join("; ", errors.Take(3))}");
        }

        var nextId = Math.Max(board.NextId, board.Cards.Select(c => BoardRules.ParseNumber(c.Id) ?? 0).DefaultIfEmpty(0).Max() + 1);
        return new BoardPackage(source, nextId, board.Cards, attachments ?? [], problems);
    }

    private static List<string> Validate(List<WebCard> cards)
    {
        var errors = new List<string>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            var label = string.IsNullOrEmpty(card.Id) ? "a card" : card.Id;
            if (BoardRules.ParseNumber(card.Id) is null || !card.Id.StartsWith(BoardCard.KeyPrefix, StringComparison.Ordinal))
            {
                errors.Add($"{label}: invalid id");
            }
            else if (!ids.Add(card.Id))
            {
                errors.Add($"{label}: duplicate id");
            }

            if (string.IsNullOrWhiteSpace(card.Uid) || card.Uid.Length > BoardCard.UidMaxLength || !uids.Add(card.Uid))
            {
                errors.Add($"{label}: missing or duplicate uid");
            }

            if (string.IsNullOrWhiteSpace(card.Title) || card.Title.Trim().Length > BoardCard.TitleMaxLength)
            {
                errors.Add($"{label}: invalid title");
            }

            if (ParseStatus(card.Status) is null || ParsePriority(card.Priority) is null
                || (card.Category is not null && ParseCategory(card.Category) is null))
            {
                errors.Add($"{label}: unknown status, priority or category");
            }

            if (ParseTimestamp(card.CreatedAt) is null || ParseTimestamp(card.UpdatedAt) is null)
            {
                errors.Add($"{label}: invalid timestamps");
            }

            card.Acceptance ??= [];
            card.BlockedBy ??= [];
            card.Links ??= [];
            card.Comments ??= [];
            card.History ??= [];
            card.Description ??= "";
            card.Notes ??= "";
            card.Evidence ??= "";
            if (card.Links.Any(l => !BoardRules.IsWebUrl(l.Url)) || card.Comments.Any(c => string.IsNullOrEmpty(c.Id) || ParseTimestamp(c.At) is null)
                || card.History.Any(h => string.IsNullOrEmpty(h.Id) || ParseTimestamp(h.At) is null))
            {
                errors.Add($"{label}: invalid link, comment or history entry");
            }
        }

        return errors;
    }

    private static BoardPackage ReadZip(string source, byte[] bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            byte[]? Entry(string name)
            {
                var entry = zip.GetEntry(name);
                if (entry is null)
                {
                    return null;
                }

                if (entry.Length > MaxAttachmentBytes * 4)
                {
                    throw new OperationNotAllowedException($"{name} in {source} is too large.");
                }

                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }

            var manifestBytes = Entry("manifest.json") ?? throw new OperationNotAllowedException($"{source} is not a WorkTrace backup (manifest.json is missing).");
            using var manifest = JsonDocument.Parse(manifestBytes);
            var root = manifest.RootElement;
            if (!root.TryGetProperty("format", out var format) || format.GetString() != BackupFormat)
            {
                throw new OperationNotAllowedException($"{source} is not a WorkTrace backup.");
            }

            if (root.TryGetProperty("formatVersion", out var version) && version.GetInt32() > BackupVersion)
            {
                throw new OperationNotAllowedException($"{source} was made by a newer version of WorkTrace. Update WorkTrace and try again.");
            }

            var cardsBytes = Entry("cards.json") ?? throw new OperationNotAllowedException($"{source} is not a WorkTrace backup (cards.json is missing).");
            var problems = new List<string>();
            var attachments = new List<PackageAttachment>();
            if (root.TryGetProperty("attachments", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var meta in list.EnumerateArray())
                {
                    string Str(string key) => meta.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var name = Str("name");
                    var cardId = Str("cardId");
                    var file = Entry(Str("path"));
                    if (file is null)
                    {
                        problems.Add($"Screenshot {name} of {cardId} is missing from the ZIP.");
                        continue;
                    }

                    var hash = Sha256(file);
                    if (!string.Equals(hash, Str("sha256"), StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"Screenshot {name} of {cardId} is damaged (checksum mismatch); it will not be restored.");
                        continue;
                    }

                    attachments.Add(new PackageAttachment(Str("id"), Str("cardUid"), name, Str("type"), Str("caption"), ParseTimestamp(Str("createdAt")), file, hash));
                }
            }

            return ReadBoardJson(source, cardsBytes, problems, attachments);
        }
        catch (InvalidDataException)
        {
            throw new OperationNotAllowedException($"{source} is damaged or not a ZIP file.");
        }
        catch (JsonException)
        {
            throw new OperationNotAllowedException($"{source} has an invalid manifest.");
        }
        catch (InvalidOperationException)
        {
            throw new OperationNotAllowedException($"{source} has an invalid manifest.");
        }
    }
}
