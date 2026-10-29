namespace WorkTrace.Core.Entities;

/// <summary>Where a card is: captured in the Inbox, or in one of the five Kanban columns.</summary>
public enum BoardStatus
{
    Inbox = 0,
    Backlog = 1,
    Todo = 2,
    Doing = 3,
    Review = 4,
    Done = 5,
}

public enum BoardCategory
{
    Feature = 1,
    Bug = 2,
    Architecture = 3,
    Testing = 4,
    Idea = 5,
}

public enum BoardPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// A board card ("WT-042"): a development item, idea or captured note. Cards are archived, never deleted.
/// A card may be linked to a <see cref="WorkTask"/> to track time on it; the two stay separate records
/// because most cards never get tracked time and most tasks never appear on the board.
/// </summary>
public class BoardCard : IAuditedEntity, IChangeTracked
{
    public const string KeyPrefix = "WT-";
    public const int UidMaxLength = 64;
    public const int TitleMaxLength = 200;
    public const int TextMaxLength = 100_000;

    public int Id { get; set; }

    /// <summary>Card number shown as "WT-042". Unique and never reused.</summary>
    public int Number { get; set; }

    /// <summary>Random identity, preserved from the web board; tells two cards with the same number apart on import.</summary>
    public required string Uid { get; set; }

    public required string Title { get; set; }

    /// <summary>Optional: Inbox items and quick notes need no category.</summary>
    public BoardCategory? Category { get; set; }

    public BoardPriority Priority { get; set; } = BoardPriority.Normal;

    public BoardStatus Status { get; set; }

    /// <summary>Order within the column (ascending).</summary>
    public double SortOrder { get; set; }

    public bool IsArchived { get; set; }

    public DateTime? ArchivedAtUtc { get; set; }

    public string Description { get; set; } = "";

    /// <summary>Acceptance criteria, one per line.</summary>
    public string Acceptance { get; set; } = "";

    public string Notes { get; set; } = "";

    public string Evidence { get; set; } = "";

    /// <summary>Optional task for time tracking.</summary>
    public int? WorkTaskId { get; set; }

    public WorkTask? WorkTask { get; set; }

    /// <summary>Incremented on every change (kept compatible with the web board's "rev").</summary>
    public int Revision { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ModifiedAtUtc { get; set; }

    public List<BoardLink> Links { get; set; } = [];

    public List<BoardComment> Comments { get; set; } = [];

    public List<BoardCardEvent> Events { get; set; } = [];

    public List<BoardDependency> BlockedBy { get; set; } = [];

    public List<BoardAttachment> Attachments { get; set; } = [];

    public string Key => FormatKey(Number);

    public static string FormatKey(int number) => $"{KeyPrefix}{number:000}";
}

public class BoardLink
{
    public const int LabelMaxLength = 200;
    public const int UrlMaxLength = 2000;

    public int Id { get; set; }

    public int CardId { get; set; }

    public int Position { get; set; }

    public required string Label { get; set; }

    public required string Url { get; set; }
}

/// <summary>Append-only comment.</summary>
public class BoardComment
{
    public const int AuthorMaxLength = 100;

    public int Id { get; set; }

    public required string Uid { get; set; }

    public int CardId { get; set; }

    public required string Author { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public required string Text { get; set; }
}

public enum BoardEventType
{
    Created = 0,
    Migrated = 1,
    Status = 2,
    Archived = 3,
    Restored = 4,
    Imported = 5,
}

/// <summary>Card history: creation, status changes, archiving, restoring, imports. Append-only.</summary>
public class BoardCardEvent
{
    public int Id { get; set; }

    public required string Uid { get; set; }

    public int CardId { get; set; }

    public BoardEventType Type { get; set; }

    public DateTime AtUtc { get; set; }

    public required string By { get; set; }

    public BoardStatus? From { get; set; }

    public BoardStatus? To { get; set; }

    /// <summary>Owner acceptance recorded when a card moved to Done, or another short note.</summary>
    public string? Note { get; set; }
}

/// <summary>"Card is blocked by BlockedByCard". No cycles (checked by the service).</summary>
public class BoardDependency
{
    public int Id { get; set; }

    public int CardId { get; set; }

    public int BlockedByCardId { get; set; }

    public BoardCard? BlockedByCard { get; set; }
}

/// <summary>Screenshot or other file attached to a card. The bytes live on disk only, under the data directory.</summary>
public class BoardAttachment
{
    public const int NameMaxLength = 260;
    public const int CaptionMaxLength = 500;

    public int Id { get; set; }

    public required string Uid { get; set; }

    public int CardId { get; set; }

    /// <summary>Path relative to the attachments root, using '/' separators.</summary>
    public required string RelativePath { get; set; }

    public required string OriginalName { get; set; }

    public string Caption { get; set; } = "";

    public required string ContentType { get; set; }

    public long SizeBytes { get; set; }

    public required string Sha256 { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Single-row board state: the next card number (numbers are never reused).</summary>
public class BoardState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    public int NextNumber { get; set; } = 1;
}
