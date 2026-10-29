using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Board;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.Shell;

/// <summary>What changed on the board, and who changed it (so the originator can skip reloading itself).</summary>
public sealed record BoardChange(int? CardId, object? Source);

/// <summary>
/// Board state shared by the Board and Inbox pages: the per-session Undo history and a change signal
/// that keeps both pages and the open card in sync. Call from the UI thread.
/// </summary>
public sealed partial class BoardSession(IBoardService board, INotificationService notifications, ILogger<BoardSession> logger) : ObservableObject
{
    private readonly BoardUndoHistory _history = new();

    public event EventHandler<BoardChange>? Changed;

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    [ObservableProperty]
    public partial string UndoToolTip { get; private set; } = "Nothing to undo";

    /// <summary>Records an undoable edit (null = nothing changed) and announces the change.</summary>
    public void Record(CardEdit? edit, object? source = null)
    {
        if (edit is null)
        {
            return;
        }

        _history.Push(edit);
        UpdateUndoState();
        Changed?.Invoke(this, new BoardChange(edit.CardId, source));
    }

    /// <summary>Announces a change that cannot be undone (new card, comment, attachment, import).</summary>
    public void Announce(int? cardId = null, object? source = null) => Changed?.Invoke(this, new BoardChange(cardId, source));

    public async Task UndoAsync()
    {
        if (_history.Pop() is not { } edit)
        {
            return;
        }

        try
        {
            await board.UndoAsync(edit);
            notifications.Show($"Undone: {Describe(edit)}", NotificationKind.Success);
        }
        catch (OperationNotAllowedException ex)
        {
            notifications.Show(ex.Message, NotificationKind.Warning);
        }
        catch (EntityNotFoundException ex)
        {
            notifications.Show(ex.Message, NotificationKind.Warning);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Undo failed");
            notifications.Show("Undo failed. See the log for details.", NotificationKind.Error);
        }

        UpdateUndoState();
        Changed?.Invoke(this, new BoardChange(edit.CardId, this));
    }

    private void UpdateUndoState()
    {
        CanUndo = _history.CanUndo;
        UndoToolTip = _history.Peek is { } next ? $"Undo {Describe(next)} (Ctrl+Z)" : "Nothing to undo";
    }

    private static string Describe(CardEdit edit) =>
        edit.Description == BoardUndoHistory.FieldsEdit ? $"edit of {edit.Key}" : $"{edit.Description.ToLowerInvariant()} ({edit.Key})";
}
