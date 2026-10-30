using WorkTrace.Core.Entities;

namespace WorkTrace.Core.Board;

/// <summary>
/// The Kanban board and Inbox. Every change is saved at once. Edits that can be undone return a
/// <see cref="CardEdit"/>; <see cref="UndoAsync"/> reverses one if the card has not changed since.
/// Cards are archived, never deleted; comments and history are append-only.
/// </summary>
public interface IBoardService
{
    /// <summary>Cards matching the filter, ordered by status, then position.</summary>
    Task<IReadOnlyList<CardSummary>> GetCardsAsync(CardFilter filter, CancellationToken cancellationToken = default);

    Task<CardDetail> GetCardAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Cards that can be chosen as dependencies or links (all, including archived).</summary>
    Task<IReadOnlyList<CardRef>> GetCardRefsAsync(CancellationToken cancellationToken = default);

    /// <summary>Quick capture into the Inbox. A URL alone becomes the title and a link.</summary>
    Task<CardDetail> CaptureAsync(string? text, string? url = null, CancellationToken cancellationToken = default);

    /// <summary>New card at the end of a column (or the Inbox).</summary>
    Task<CardDetail> CreateAsync(CardFields fields, BoardStatus status, CancellationToken cancellationToken = default);

    /// <returns>The edit, or null when nothing changed.</returns>
    Task<CardEdit?> UpdateFieldsAsync(int id, CardFields fields, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the card to <paramref name="status"/>, before <paramref name="beforeCardId"/> (null = at the end).
    /// Moving a feature, bug or uncategorised card to Done records <paramref name="acceptance"/> in its history.
    /// </summary>
    Task<CardEdit?> MoveAsync(int id, BoardStatus status, int? beforeCardId, string? acceptance = null, CancellationToken cancellationToken = default);

    Task<CardEdit?> SetArchivedAsync(int id, bool isArchived, CancellationToken cancellationToken = default);

    Task<CardEdit?> SetLinksAsync(int id, IReadOnlyList<LinkInput> links, CancellationToken cancellationToken = default);

    /// <exception cref="Validation.DomainValidationException">Unknown card, self-reference or a cycle.</exception>
    Task<CardEdit?> SetBlockedByAsync(int id, IReadOnlyList<int> blockedByIds, CancellationToken cancellationToken = default);

    Task<CardEdit?> SetWorkTaskAsync(int id, int? workTaskId, CancellationToken cancellationToken = default);

    Task<CommentItem> AddCommentAsync(int id, string? text, CancellationToken cancellationToken = default);

    /// <summary>Archives every non-archived Done card; returns one edit per card.</summary>
    Task<IReadOnlyList<CardEdit>> ArchiveDoneAsync(CancellationToken cancellationToken = default);

    /// <summary>Restores the state before <paramref name="edit"/>.</summary>
    /// <exception cref="Validation.OperationNotAllowedException">The card changed since the edit.</exception>
    Task<CardEdit> UndoAsync(CardEdit edit, CancellationToken cancellationToken = default);

    // ---- Cards of time-tracking tasks (time tracking never depends on these) ----

    /// <summary>
    /// Shows the task's linked card on the board (restoring an archived one, so its column and history are kept,
    /// or creating one in To do) or takes it off the board (archives it). Nothing happens to the task or its time.
    /// </summary>
    Task SetTaskOnBoardAsync(int taskId, bool onBoard, CancellationToken cancellationToken = default);

    /// <summary>Keeps the titles of the task's linked cards equal to the task title.</summary>
    Task SyncTaskTitleAsync(int taskId, CancellationToken cancellationToken = default);

    // ---- Attachments ----

    /// <summary>Copies the bytes into the data directory and attaches them to the card.</summary>
    Task<AttachmentItem> AddAttachmentAsync(int cardId, string originalName, string contentType, byte[] bytes, string? caption = null, CancellationToken cancellationToken = default);

    Task<AttachmentItem> SetAttachmentCaptionAsync(int attachmentId, string? caption, CancellationToken cancellationToken = default);

    /// <summary>Removes the attachment and its file.</summary>
    Task RemoveAttachmentAsync(int attachmentId, CancellationToken cancellationToken = default);

    // ---- Import (board.json from the web board, or a board backup ZIP) ----

    Task<ImportPreview> PreviewImportAsync(string path, CancellationToken cancellationToken = default);

    /// <param name="overwriteUids">Cards marked <see cref="ImportItemKind.LocalNewer"/> to overwrite anyway.</param>
    Task<ImportResult> ImportAsync(string path, IReadOnlyCollection<string> overwriteUids, CancellationToken cancellationToken = default);
}
