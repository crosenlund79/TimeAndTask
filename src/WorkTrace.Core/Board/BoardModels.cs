using WorkTrace.Core.Entities;

namespace WorkTrace.Core.Board;

// ---- Inputs ----

/// <summary>The card's editable text fields, saved together (auto-save sends the whole set).</summary>
public sealed record CardFields(
    string? Title,
    BoardCategory? Category,
    BoardPriority Priority,
    string? Description,
    string? Acceptance,
    string? Notes,
    string? Evidence);

public sealed record LinkInput(string? Label, string? Url);

/// <summary>Search and filters. Text matches key, title, description, acceptance, notes, evidence and comments.</summary>
public sealed record CardFilter(
    string? Text = null,
    bool Archived = false,
    IReadOnlyCollection<BoardStatus>? Statuses = null,
    BoardCategory? Category = null,
    BoardPriority? Priority = null);

// ---- Read models ----

public sealed record CardRef(int Id, string Key, string Title, BoardStatus Status, bool IsArchived);

public sealed record CardSummary(
    int Id,
    int Number,
    string Key,
    string Title,
    BoardCategory? Category,
    BoardPriority Priority,
    BoardStatus Status,
    double SortOrder,
    bool IsArchived,
    DateTime? ArchivedAtUtc,
    IReadOnlyList<CardRef> BlockedBy,
    int CommentCount,
    int LinkCount,
    int AttachmentCount,
    int? WorkTaskId,
    DateTime CreatedAtUtc,
    DateTime ModifiedAtUtc)
{
    /// <summary>Blocked while any card it waits for is not Done.</summary>
    public bool IsBlocked => BlockedBy.Any(b => b.Status != BoardStatus.Done);
}

public sealed record CommentItem(int Id, string Author, DateTime CreatedAtUtc, string Text);

public sealed record CardEventItem(BoardEventType Type, DateTime AtUtc, string By, BoardStatus? From, BoardStatus? To, string? Note);

public sealed record AttachmentItem(
    int Id,
    string Uid,
    int CardId,
    string OriginalName,
    string Caption,
    string ContentType,
    long SizeBytes,
    string FullPath,
    DateTime CreatedAtUtc);

public sealed record CardDetail(
    CardSummary Summary,
    string Description,
    string Acceptance,
    string Notes,
    string Evidence,
    IReadOnlyList<LinkInput> Links,
    IReadOnlyList<CommentItem> Comments,
    IReadOnlyList<CardEventItem> Events,
    IReadOnlyList<AttachmentItem> Attachments,
    string? WorkTaskTitle,
    int Revision)
{
    public int Id => Summary.Id;

    public string Key => Summary.Key;

    public string Title => Summary.Title;

    public BoardStatus Status => Summary.Status;
}

// ---- Undo ----

/// <summary>Everything an undoable edit can change on one card.</summary>
public sealed record CardState(
    string Title,
    BoardCategory? Category,
    BoardPriority Priority,
    BoardStatus Status,
    double SortOrder,
    bool IsArchived,
    string Description,
    string Acceptance,
    string Notes,
    string Evidence,
    int? WorkTaskId,
    IReadOnlyList<LinkInput> Links,
    IReadOnlyList<int> BlockedByIds)
{
    /// <summary>Structural equality (records compare lists by reference).</summary>
    public bool SameAs(CardState other) =>
        Title == other.Title && Category == other.Category && Priority == other.Priority && Status == other.Status
        && SortOrder.Equals(other.SortOrder) && IsArchived == other.IsArchived && Description == other.Description
        && Acceptance == other.Acceptance && Notes == other.Notes && Evidence == other.Evidence
        && WorkTaskId == other.WorkTaskId && Links.SequenceEqual(other.Links)
        && BlockedByIds.Order().SequenceEqual(other.BlockedByIds.Order());
}

/// <summary>One saved, undoable change to a card: undo restores <see cref="Before"/> if the card still equals <see cref="After"/>.</summary>
public sealed record CardEdit(int CardId, string Key, string Description, CardState Before, CardState After);

// ---- Import ----

public enum ImportItemKind
{
    /// <summary>Not on this board: will be added.</summary>
    New = 0,

    /// <summary>Identical to the card here: nothing to do.</summary>
    Same = 1,

    /// <summary>Differs and the card here was not edited after the imported version: will be updated.</summary>
    Changed = 2,

    /// <summary>The card here was edited after the imported version: kept unless chosen explicitly.</summary>
    LocalNewer = 3,

    /// <summary>A different card already uses this number: added with a new number.</summary>
    NumberTaken = 4,
}

public sealed record ImportItem(string Uid, string Key, string Title, ImportItemKind Kind, string? NewKey, int NewComments, int NewAttachments);

public sealed record ImportPreview(
    string Source,
    IReadOnlyList<ImportItem> Items,
    IReadOnlyList<string> Problems,
    int AttachmentCount,
    bool IncludesDatabase = false)
{
    public int Count(ImportItemKind kind) => Items.Count(i => i.Kind == kind);
}

public sealed record ImportResult(int Added, int Updated, int Unchanged, int Skipped, int AttachmentsAdded, IReadOnlyList<string> Notes);
