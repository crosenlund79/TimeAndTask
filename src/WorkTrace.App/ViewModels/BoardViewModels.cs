using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>Navigation parameter: open this card.</summary>
public sealed record OpenCardRequest(int CardId);

/// <summary>
/// Shared behaviour of the Board and Inbox pages: quick capture, the open card panel, Undo, and reloading
/// when the board changes elsewhere. Everything saves at once, so there are never unsaved changes.
/// </summary>
public abstract partial class BoardPageViewModel : ViewModelBase, ISectionViewModel, IDisposable
{
    private CancellationTokenSource? _searchCts;

    protected BoardPageViewModel(CardPanelServices services)
        : base(services.Notifications, services.Logger)
    {
        Services = services;
        Session.Changed += OnBoardChanged;
    }

    protected CardPanelServices Services { get; }

    protected IBoardService Board => Services.Board;

    public BoardSession Session => Services.Session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial CardDetailViewModel? Detail { get; private set; }

    public bool HasDetail => Detail is not null;

    partial void OnDetailChanged(CardDetailViewModel? value)
    {
        if (value is null)
        {
            OnCardClosed();
        }
    }

    /// <summary>Clears the list selection so the same card can be opened again.</summary>
    protected abstract void OnCardClosed();

    public bool HasUnsavedChanges => false;

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    [ObservableProperty]
    public partial string? EmptyMessage { get; protected set; }

    // ---- Quick capture ----

    [ObservableProperty]
    public partial string? CaptureText { get; set; }

    /// <summary>A pasted screenshot waiting to be captured with the next item.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaptureImage))]
    public partial byte[]? CaptureImage { get; private set; }

    public bool HasCaptureImage => CaptureImage is not null;

    public void SetCaptureImage(byte[]? png) => CaptureImage = png;

    [RelayCommand]
    private void ClearCaptureImage() => CaptureImage = null;

    [RelayCommand]
    private async Task CaptureAsync()
    {
        var image = CaptureImage;
        var text = Text.Clean(CaptureText) ?? (image is null ? null : $"Screenshot {DateTime.Now:yyyy-MM-dd HH:mm}");
        try
        {
            CardDetail? card = null;
            await RunAsync(async () =>
            {
                card = await Board.CaptureAsync(text);
                if (image is not null)
                {
                    await Board.AddAttachmentAsync(card.Id, "screenshot.png", "image/png", image);
                }
            }, "Could not capture the item.");
            if (card is not null)
            {
                CaptureText = null;
                CaptureImage = null;
                Session.Announce(card.Id, this);
                await ReloadAsync();
                Notifications.Show($"Captured to the Inbox as {card.Key}.", NotificationKind.Success);
            }
        }
        catch (DomainValidationException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
        }
    }

    // ---- Open card ----

    public async Task OpenCardAsync(int? id)
    {
        if (Detail?.Id == id && id is not null)
        {
            return;
        }

        if (Detail is { } previous)
        {
            await previous.FlushAsync();
            previous.Dispose();
        }

        Detail = null;
        if (id is { } cardId)
        {
            var detail = new CardDetailViewModel(Services);
            await detail.LoadAsync(cardId);
            Detail = detail.Detail is null ? null : detail;
        }
    }

    [RelayCommand]
    private Task CloseCardAsync() => OpenCardAsync(null);

    [RelayCommand]
    private Task UndoAsync() => Session.UndoAsync();

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    // ---- Loading ----

    public async Task ActivateAsync(object? parameter)
    {
        await ReloadAsync();
        if (parameter is OpenCardRequest open)
        {
            await OpenCardAsync(open.CardId);
        }
        else if (Detail is not null)
        {
            await Detail.RefreshAsync();
        }
    }

    public Task ReloadAsync() => RunAsync(LoadAsync, "Could not load the board.");

    protected abstract Task LoadAsync();

    partial void OnSearchTextChanged(string? value) => ScheduleReload();

    protected void ScheduleReload()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Delay(150, token).ContinueWith(
            _ => ReloadAsync(), token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnBoardChanged(object? sender, BoardChange change)
    {
        if (!ReferenceEquals(change.Source, this))
        {
            _ = ReloadAsync();
        }
    }

    /// <summary>Updates a list in place when it holds the same cards in the same order (keeps selection and focus), else rebuilds it.</summary>
    protected static void Sync(ObservableCollection<CardItemViewModel> target, IReadOnlyList<CardSummary> cards)
    {
        if (target.Select(c => c.Id).SequenceEqual(cards.Select(c => c.Id)))
        {
            for (var i = 0; i < cards.Count; i++)
            {
                if (target[i].Summary != cards[i])
                {
                    target[i].Summary = cards[i];
                }
            }

            return;
        }

        var existing = target.ToDictionary(c => c.Id);
        target.Clear();
        foreach (var card in cards)
        {
            if (existing.TryGetValue(card.Id, out var item))
            {
                item.Summary = card;
                target.Add(item);
            }
            else
            {
                target.Add(new CardItemViewModel(card));
            }
        }
    }

    public virtual void Dispose()
    {
        Session.Changed -= OnBoardChanged;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        Detail?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>The Kanban board: five columns, drag-and-drop, filters, the archive and Undo.</summary>
public sealed partial class BoardViewModel : BoardPageViewModel
{
    public BoardViewModel(CardPanelServices services)
        : base(services)
    {
        Columns = [.. BoardRules.Columns.Select(s => new BoardColumnViewModel(s))];
    }

    public bool ShowColumns => !ShowArchive;

    public IReadOnlyList<BoardColumnViewModel> Columns { get; }

    public ObservableCollection<CardItemViewModel> ArchiveItems { get; } = [];

    public IReadOnlyList<PickerOption> CategoryFilterOptions { get; } = CardText.CategoryOptions("All categories");

    public IReadOnlyList<PickerOption> PriorityFilterOptions { get; } =
        [new(PickerOption.NoneKey, "All priorities"), .. CardText.PriorityOptions()];

    [ObservableProperty]
    public partial int CategoryFilterKey { get; set; } = PickerOption.NoneKey;

    [ObservableProperty]
    public partial int PriorityFilterKey { get; set; } = PickerOption.NoneKey;

    /// <summary>Shows the archive (searchable list with Restore) instead of the columns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowColumns))]
    public partial bool ShowArchive { get; set; }

    [ObservableProperty]
    public partial CardItemViewModel? SelectedCard { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    protected override void OnCardClosed() => SelectedCard = null;

    partial void OnCategoryFilterKeyChanged(int value) => ScheduleReload();

    partial void OnPriorityFilterKeyChanged(int value) => ScheduleReload();

    partial void OnShowArchiveChanged(bool value)
    {
        _ = ReloadAsync();
    }

    partial void OnSelectedCardChanged(CardItemViewModel? value)
    {
        if (value is not null)
        {
            _ = OpenCardAsync(value.Id);
        }
    }

    protected override async Task LoadAsync()
    {
        var filter = new CardFilter(
            SearchText,
            Archived: ShowArchive,
            Statuses: ShowArchive ? null : BoardRules.Columns,
            Category: CardText.CategoryFromKey(CategoryFilterKey),
            Priority: PriorityFilterKey == PickerOption.NoneKey ? null : CardText.PriorityFromKey(PriorityFilterKey));
        var cards = await Board.GetCardsAsync(filter);

        if (ShowArchive)
        {
            Sync(ArchiveItems, [.. cards.OrderByDescending(c => c.ArchivedAtUtc).ThenByDescending(c => c.Number)]);
            Summary = $"{cards.Count} archived card{(cards.Count == 1 ? "" : "s")}";
            EmptyMessage = cards.Count == 0 ? (string.IsNullOrWhiteSpace(SearchText) ? "The archive is empty." : "No archived card matches.") : null;
            return;
        }

        foreach (var column in Columns)
        {
            var inColumn = cards.Where(c => c.Status == column.Status).ToList();
            Sync(column.Cards, inColumn);
            column.Count = inColumn.Count;
        }

        var filtered = !string.IsNullOrWhiteSpace(SearchText) || CategoryFilterKey != PickerOption.NoneKey || PriorityFilterKey != PickerOption.NoneKey;
        Summary = filtered ? $"{cards.Count} matching card{(cards.Count == 1 ? "" : "s")}" : null;
        EmptyMessage = null;
    }

    /// <summary>
    /// Drag-and-drop and keyboard moves: puts the card into <paramref name="status"/> before
    /// <paramref name="beforeCardId"/> (null = at the end).
    /// </summary>
    public async Task MoveCardAsync(int cardId, BoardStatus status, int? beforeCardId)
    {
        var card = Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Id == cardId);
        if (card is null)
        {
            return;
        }

        if (beforeCardId == cardId)
        {
            // Dropped onto itself: "before the card that follows it", i.e. where it already is.
            var column = Columns.Single(c => c.Status == card.Status).Cards;
            var index = column.IndexOf(card);
            beforeCardId = status == card.Status && index + 1 < column.Count ? column[index + 1].Id : null;
            if (status == card.Status && index + 1 >= column.Count)
            {
                return;
            }
        }

        if (Detail?.Id == cardId)
        {
            await Detail.FlushAsync();
        }

        var acceptance = CardDetailViewModel.AskAcceptance(Services.Dialogs, card.Key, card.Title, card.Summary.Category, card.Status, status);
        if (acceptance == CardDetailViewModel.CancelledAcceptance)
        {
            return;
        }

        if (status == BoardStatus.Doing && card.Status != BoardStatus.Doing && card.IsBlocked
            && !Services.Dialogs.Confirm("Card is blocked", $"{card.Key} is {card.BlockedText.ToLowerInvariant()}. Start it anyway?", "Start anyway"))
        {
            return;
        }

        await RunAsync(async () => Session.Record(await Board.MoveAsync(cardId, status, beforeCardId, acceptance), this), "Could not move the card.");
        await ReloadAsync();
        if (Detail?.Id == cardId)
        {
            await Detail.RefreshAsync();
        }
    }

    /// <summary>Alt+arrow keys: ←/→ change column (to the top of the target), ↑/↓ change order.</summary>
    public async Task MoveByKeyAsync(CardItemViewModel card, int columnDelta, int rowDelta)
    {
        var columnIndex = Columns.ToList().FindIndex(c => c.Status == card.Status);
        if (columnIndex < 0)
        {
            return;
        }

        if (columnDelta != 0)
        {
            var target = columnIndex + columnDelta;
            if (target >= 0 && target < Columns.Count)
            {
                var first = Columns[target].Cards.FirstOrDefault();
                await MoveCardAsync(card.Id, Columns[target].Status, first?.Id);
            }

            return;
        }

        var cards = Columns[columnIndex].Cards;
        var index = cards.IndexOf(card);
        var newIndex = index + rowDelta;
        if (index < 0 || newIndex < 0 || newIndex >= cards.Count)
        {
            return;
        }

        // Moving down: insert before the card after the next one.
        int? before = rowDelta < 0 ? cards[newIndex].Id : newIndex + 1 < cards.Count ? cards[newIndex + 1].Id : null;
        await MoveCardAsync(card.Id, card.Status, before);
    }

    [RelayCommand]
    private async Task ArchiveDoneAsync()
    {
        var count = Columns.Single(c => c.IsDone).Cards.Count;
        if (count == 0 || !Services.Dialogs.Confirm("Archive Done cards", $"Archive all {count} cards in Done? They stay searchable under Archive and can be restored.", "Archive"))
        {
            return;
        }

        IReadOnlyList<CardEdit> edits = [];
        await RunAsync(async () => edits = await Board.ArchiveDoneAsync(), "Could not archive the cards.");
        foreach (var edit in edits)
        {
            Session.Record(edit, this);
        }

        await ReloadAsync();
        Notifications.Show($"{edits.Count} cards archived.", NotificationKind.Success);
    }

    [RelayCommand]
    private async Task RestoreAsync(CardItemViewModel card)
    {
        await RunAsync(async () => Session.Record(await Board.SetArchivedAsync(card.Id, false), this), "Could not restore the card.");
        await ReloadAsync();
        Notifications.Show($"{card.Key} restored to {BoardRules.StatusName(card.Status)}.", NotificationKind.Success, "Undo", Session.UndoAsync);
    }

    [RelayCommand]
    private void ToggleArchive() => ShowArchive = !ShowArchive;
}

/// <summary>The Inbox: captured items that are not on the board yet, newest first.</summary>
public sealed partial class InboxViewModel(CardPanelServices services) : BoardPageViewModel(services)
{
    public ObservableCollection<CardItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial CardItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; private set; }

    protected override void OnCardClosed() => SelectedItem = null;

    partial void OnSelectedItemChanged(CardItemViewModel? value)
    {
        if (value is not null)
        {
            _ = OpenCardAsync(value.Id);
        }
    }

    protected override async Task LoadAsync()
    {
        var cards = await Board.GetCardsAsync(new CardFilter(SearchText, Statuses: [BoardStatus.Inbox]));
        Sync(Items, [.. cards.OrderByDescending(c => c.CreatedAtUtc).ThenByDescending(c => c.Number)]);
        Summary = cards.Count == 0 ? null : $"{cards.Count} item{(cards.Count == 1 ? "" : "s")}";
        EmptyMessage = cards.Count > 0 ? null
            : string.IsNullOrWhiteSpace(SearchText) ? "Inbox zero. Type above and press Enter to capture a thought, link or screenshot."
            : "Nothing in the Inbox matches.";
        if (Detail is { } detail && Items.All(i => i.Id != detail.Id) && string.IsNullOrWhiteSpace(SearchText))
        {
            await OpenCardAsync(null); // moved to the board or archived
        }
    }

    [RelayCommand]
    private async Task ToBoardAsync(CardItemViewModel item)
    {
        await RunAsync(async () => Session.Record(await Board.MoveAsync(item.Id, BoardStatus.Backlog, null), this), "Could not move the item.");
        await ReloadAsync();
        Notifications.Show($"{item.Key} moved to the Backlog.", NotificationKind.Success, "Undo", Session.UndoAsync);
    }

    [RelayCommand]
    private async Task ArchiveAsync(CardItemViewModel item)
    {
        await RunAsync(async () => Session.Record(await Board.SetArchivedAsync(item.Id, true), this), "Could not archive the item.");
        await ReloadAsync();
        Notifications.Show($"{item.Key} archived.", NotificationKind.Success, "Undo", Session.UndoAsync);
    }
}
