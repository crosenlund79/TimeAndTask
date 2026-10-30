using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Board;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

public sealed record LinkItemViewModel(string Label, string Url);

public sealed record HistoryLineViewModel(string When, string Text);

public sealed record CommentLineViewModel(string Author, string When, string Text);

public sealed record DependencyItemViewModel(CardRef Card)
{
    public string Key => Card.Key;

    public string Title => Card.Title;

    public string StatusText => Card.IsArchived ? "Archived" : BoardRules.StatusName(Card.Status);
}

/// <summary>Services the card panel needs (one bundle, so pages can create panels cheaply).</summary>
public sealed record CardPanelServices(
    IBoardService Board,
    BoardSession Session,
    ITaskService Tasks,
    IClientService Clients,
    IDialogService Dialogs,
    IFileDialogService Files,
    INotificationService Notifications,
    ILogger<CardDetailViewModel> Logger);

/// <summary>
/// The open card (right-hand panel on Board and Inbox). Text fields save automatically shortly after
/// typing stops; every other change saves at once. Saved edits go to the shared Undo history.
/// </summary>
public sealed partial class CardDetailViewModel : ViewModelBase, IDisposable
{
    /// <summary>Delay between the last keystroke and the automatic save.</summary>
    public static readonly TimeSpan AutoSaveDelay = TimeSpan.FromMilliseconds(700);

    private readonly CardPanelServices _s;
    private CancellationTokenSource? _saveCts;
    private Task _pendingSave = Task.CompletedTask;
    private bool _loading;
    private bool _fieldsDirty;
    private bool _disposed;

    public CardDetailViewModel(CardPanelServices services)
        : base(services.Notifications, services.Logger)
    {
        _s = services;
        _s.Session.Changed += OnBoardChanged;
    }

    public int Id { get; private set; }

    [ObservableProperty]
    public partial string Key { get; private set; } = "";

    [ObservableProperty]
    public partial CardDetail? Detail { get; private set; }

    // ---- Auto-saved fields ----

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    public partial string Acceptance { get; set; } = "";

    [ObservableProperty]
    public partial string Notes { get; set; } = "";

    [ObservableProperty]
    public partial string Evidence { get; set; } = "";

    [ObservableProperty]
    public partial int CategoryKey { get; set; }

    [ObservableProperty]
    public partial int PriorityKey { get; set; } = CardText.KeyOf(BoardPriority.Normal);

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    /// <summary>"Saved", "Saving…" or a problem; shown under the panel header.</summary>
    [ObservableProperty]
    public partial string SaveState { get; private set; } = "Saved";

    // ---- Saved at once ----

    [ObservableProperty]
    public partial int StatusKey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInbox))]
    public partial BoardStatus Status { get; private set; }

    [ObservableProperty]
    public partial bool IsArchived { get; private set; }

    public bool IsInbox => Status == BoardStatus.Inbox;

    [ObservableProperty]
    public partial string Meta { get; private set; } = "";

    public IReadOnlyList<PickerOption> CategoryOptions { get; } = CardText.CategoryOptions("None");

    public IReadOnlyList<PickerOption> PriorityOptions { get; } = CardText.PriorityOptions();

    public IReadOnlyList<PickerOption> StatusOptions { get; } = CardText.StatusOptions();

    public ObservableCollection<LinkItemViewModel> Links { get; } = [];

    [ObservableProperty]
    public partial string? NewLinkLabel { get; set; }

    [ObservableProperty]
    public partial string? NewLinkUrl { get; set; }

    [ObservableProperty]
    public partial string? LinkError { get; private set; }

    public ObservableCollection<DependencyItemViewModel> BlockedBy { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<PickerOption> DependencyOptions { get; private set; } = [];

    [ObservableProperty]
    public partial int DependencyKey { get; set; }

    [ObservableProperty]
    public partial string? DependencyError { get; private set; }

    public ObservableCollection<CommentLineViewModel> Comments { get; } = [];

    [ObservableProperty]
    public partial string? NewComment { get; set; }

    public ObservableCollection<HistoryLineViewModel> History { get; } = [];

    public ObservableCollection<AttachmentItemViewModel> Attachments { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<PickerOption> TaskOptions { get; private set; } = [];

    [ObservableProperty]
    public partial int TaskKey { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PickerOption> ClientOptions { get; private set; } = [];

    [ObservableProperty]
    public partial int ClientKey { get; set; }

    /// <summary>True while an automatic save is waiting or running.</summary>
    public bool HasPendingSave => _fieldsDirty || !_pendingSave.IsCompleted;

    // ---- Loading ----

    public async Task LoadAsync(int id)
    {
        Id = id;
        await RunAsync(async () =>
        {
            var detail = await _s.Board.GetCardAsync(id);
            Apply(detail, includeFields: true);
            await LoadPickersAsync();
        }, "Could not open the card.");
    }

    /// <summary>Reloads after a change made elsewhere. Text being edited here is kept.</summary>
    public async Task RefreshAsync()
    {
        await RunAsync(async () => Apply(await _s.Board.GetCardAsync(Id), includeFields: !HasPendingSave), "Could not refresh the card.");
    }

    private void Apply(CardDetail detail, bool includeFields)
    {
        _loading = true;
        try
        {
            Detail = detail;
            Key = detail.Key;
            Status = detail.Status;
            StatusKey = CardText.KeyOf(detail.Status);
            IsArchived = detail.Summary.IsArchived;
            Meta = detail.Summary.IsArchived && detail.Summary.ArchivedAtUtc is { } archived
                ? $"Archived {CardText.DateTime(archived)} · created {CardText.DateTime(detail.Summary.CreatedAtUtc)}"
                : $"Created {CardText.DateTime(detail.Summary.CreatedAtUtc)} · changed {CardText.DateTime(detail.Summary.ModifiedAtUtc)}";
            if (includeFields)
            {
                Title = detail.Title;
                Description = detail.Description;
                Acceptance = detail.Acceptance;
                Notes = detail.Notes;
                Evidence = detail.Evidence;
                CategoryKey = CardText.KeyOf(detail.Summary.Category);
                PriorityKey = CardText.KeyOf(detail.Summary.Priority);
                TitleError = null;
                SaveState = "Saved";
            }

            Replace(Links, detail.Links.Select(l => new LinkItemViewModel(l.Label!, l.Url!)));
            Replace(BlockedBy, detail.Summary.BlockedBy.Select(b => new DependencyItemViewModel(b)));
            Replace(Comments, detail.Comments.Select(c => new CommentLineViewModel(c.Author, CardText.DateTime(c.CreatedAtUtc), c.Text)));
            Replace(History, detail.Events.Reverse().Select(e => new HistoryLineViewModel(CardText.DateTime(e.AtUtc), HistoryText(e))));
            Replace(Attachments, detail.Attachments.Select(a => new AttachmentItemViewModel(a)));
            TaskKey = detail.Summary.WorkTaskId ?? PickerOption.NoneKey;
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task LoadPickersAsync()
    {
        var refs = await _s.Board.GetCardRefsAsync();
        DependencyOptions = [new(PickerOption.NoneKey, "Add a card it waits for…"), .. refs.Where(r => r.Id != Id).Select(r => new PickerOption(r.Id, $"{r.Key} {r.Title}", !r.IsArchived))];
        DependencyKey = PickerOption.NoneKey;

        var tasks = await _s.Tasks.SearchAsync(new TaskQuery(null, null, IncludeArchived: false, Limit: 200));
        var linked = Detail?.Summary.WorkTaskId;
        var options = new List<PickerOption> { new(PickerOption.NoneKey, "Not tracked") };
        options.AddRange(tasks.Select(t => new PickerOption(t.Id, $"{t.Title} · {t.ClientName}")));
        if (linked is { } taskId && options.All(o => o.Id != taskId))
        {
            options.Add(new PickerOption(taskId, Detail?.WorkTaskTitle ?? $"Task {taskId}", IsActive: false));
        }

        _loading = true;
        TaskOptions = options;
        TaskKey = linked ?? PickerOption.NoneKey;
        _loading = false;

        var clients = await _s.Clients.GetClientsForPickerAsync();
        ClientOptions = [.. clients.Select(c => new PickerOption(c.Id, c.Name))];
        ClientKey = ClientOptions.Count > 0 ? ClientOptions[0].Id : PickerOption.NoneKey;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static string HistoryText(CardEventItem e)
    {
        var text = e.Type switch
        {
            BoardEventType.Created => e.To is { } to ? $"Created in {BoardRules.StatusName(to)}" : "Created",
            BoardEventType.Migrated => e.To is { } to ? $"Migrated to {BoardRules.StatusName(to)}" : "Migrated",
            BoardEventType.Status => $"{(e.From is { } from ? BoardRules.StatusName(from) : "?")} → {(e.To is { } to ? BoardRules.StatusName(to) : "?")}",
            BoardEventType.Archived => "Archived",
            BoardEventType.Restored => "Restored from the archive",
            BoardEventType.Imported => "Imported",
            _ => e.Type.ToString(),
        };
        var note = string.IsNullOrEmpty(e.Note) ? "" : $" — {e.Note}";
        return $"{text}{note} · {e.By}";
    }

    // ---- Auto-save ----

    partial void OnTitleChanged(string value) => FieldChanged();

    partial void OnDescriptionChanged(string value) => FieldChanged();

    partial void OnAcceptanceChanged(string value) => FieldChanged();

    partial void OnNotesChanged(string value) => FieldChanged();

    partial void OnEvidenceChanged(string value) => FieldChanged();

    partial void OnCategoryKeyChanged(int value) => FieldChanged(immediately: true);

    partial void OnPriorityKeyChanged(int value) => FieldChanged(immediately: true);

    private void FieldChanged(bool immediately = false)
    {
        if (_loading || Detail is null)
        {
            return;
        }

        _fieldsDirty = true;
        SaveState = "Saving…";
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = SaveSoonAsync(immediately ? TimeSpan.Zero : AutoSaveDelay, token);
    }

    private async Task SaveSoonAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, token);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await FlushAsync();
    }

    /// <summary>Saves pending field edits now (called before switching cards or pages).</summary>
    public async Task FlushAsync()
    {
        await _pendingSave;
        if (!_fieldsDirty || Detail is null)
        {
            return;
        }

        _saveCts?.Cancel();
        _pendingSave = SaveFieldsAsync();
        await _pendingSave;
    }

    private async Task SaveFieldsAsync()
    {
        _fieldsDirty = false;
        var fields = new CardFields(Title, CardText.CategoryFromKey(CategoryKey), CardText.PriorityFromKey(PriorityKey), Description, Acceptance, Notes, Evidence);
        var errors = BoardRules.Validate(fields);
        TitleError = errors.For(nameof(CardFields.Title));
        if (!errors.IsValid)
        {
            SaveState = "Not saved: " + string.Join(" ", errors.ByField.Values.SelectMany(v => v));
            _fieldsDirty = true;
            return;
        }

        try
        {
            var ok = await RunAsync(async () =>
            {
                var edit = await _s.Board.UpdateFieldsAsync(Id, fields);
                _s.Session.Record(edit, this);
            }, "Could not save the card.");
            SaveState = ok ? "Saved" : "Not saved";
        }
        catch (DomainValidationException ex)
        {
            SaveState = "Not saved: " + ex.Message;
            _fieldsDirty = true;
        }
    }

    // ---- Status, archive ----

    partial void OnStatusKeyChanged(int value)
    {
        if (_loading || Detail is null || value == CardText.KeyOf(Status))
        {
            return;
        }

        _ = MoveToAsync(CardText.StatusFromKey(value));
    }

    /// <summary>Moves the card to the end of a column (asks for acceptance before Done).</summary>
    public async Task MoveToAsync(BoardStatus status)
    {
        await FlushAsync();
        var acceptance = AskAcceptance(_s.Dialogs, Key, Title, CardText.CategoryFromKey(CategoryKey), Status, status);
        if (acceptance == CancelledAcceptance)
        {
            await RefreshAsync();
            return;
        }

        await RunAsync(async () => _s.Session.Record(await _s.Board.MoveAsync(Id, status, null, acceptance), this), "Could not move the card.");
        await RefreshAsync();
    }

    internal const string CancelledAcceptance = "\0cancelled";

    /// <summary>
    /// Moving user-facing work to Done is the owner's acceptance: asks for confirmation and returns the note
    /// to record, null when no acceptance is needed, or <see cref="CancelledAcceptance"/>.
    /// </summary>
    internal static string? AskAcceptance(IDialogService dialogs, string key, string title, BoardCategory? category, BoardStatus from, BoardStatus to)
    {
        if (to != BoardStatus.Done || from == BoardStatus.Done || !BoardRules.NeedsOwnerAcceptance(category))
        {
            return null;
        }

        return dialogs.Confirm(
            "Accept as done?",
            $"Moving {key} \"{title}\" to Done means you have tested it and accept it. This is recorded in its history.",
            "Accept and move")
            ? "Accepted by the owner in WorkTrace"
            : CancelledAcceptance;
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        await FlushAsync();
        await RunAsync(async () => _s.Session.Record(await _s.Board.SetArchivedAsync(Id, !IsArchived), this), "Could not archive the card.");
        await RefreshAsync();
        Notifications.Show(IsArchived ? $"{Key} archived. Find it under Archive." : $"{Key} restored.", NotificationKind.Success, "Undo", _s.Session.UndoAsync);
    }

    /// <summary>Inbox → board (Backlog by default).</summary>
    [RelayCommand]
    private Task ToBoardAsync() => MoveToAsync(BoardStatus.Backlog);

    // ---- Links ----

    [RelayCommand]
    private async Task AddLinkAsync()
    {
        LinkError = null;
        var url = Text.Clean(NewLinkUrl);
        if (url is null)
        {
            LinkError = "Paste a web address.";
            return;
        }

        var links = Links.Select(l => new LinkInput(l.Label, l.Url)).Append(new LinkInput(NewLinkLabel, url)).ToList();
        if (await SaveLinksAsync(links))
        {
            NewLinkLabel = null;
            NewLinkUrl = null;
        }
    }

    [RelayCommand]
    private Task<bool> RemoveLinkAsync(LinkItemViewModel link) =>
        SaveLinksAsync([.. Links.Where(l => !ReferenceEquals(l, link)).Select(l => new LinkInput(l.Label, l.Url))]);

    private async Task<bool> SaveLinksAsync(IReadOnlyList<LinkInput> links)
    {
        try
        {
            var ok = await RunAsync(async () => _s.Session.Record(await _s.Board.SetLinksAsync(Id, links), this), "Could not save the links.");
            await RefreshAsync();
            return ok;
        }
        catch (DomainValidationException ex)
        {
            LinkError = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void OpenLink(LinkItemViewModel link) => _s.Files.OpenUrl(link.Url);

    [RelayCommand]
    private void CopyLink(LinkItemViewModel link)
    {
        _s.Files.CopyText(link.Url);
        Notifications.Show("Link copied.", NotificationKind.Success);
    }

    // ---- Dependencies ----

    partial void OnDependencyKeyChanged(int value)
    {
        if (value != PickerOption.NoneKey && !_loading)
        {
            _ = SaveDependenciesAsync([.. BlockedBy.Select(b => b.Card.Id), value]);
        }
    }

    [RelayCommand]
    private Task RemoveDependencyAsync(DependencyItemViewModel item) =>
        SaveDependenciesAsync([.. BlockedBy.Where(b => b.Card.Id != item.Card.Id).Select(b => b.Card.Id)]);

    private async Task SaveDependenciesAsync(IReadOnlyList<int> ids)
    {
        DependencyError = null;
        try
        {
            await RunAsync(async () => _s.Session.Record(await _s.Board.SetBlockedByAsync(Id, ids), this), "Could not save the dependencies.");
        }
        catch (DomainValidationException ex)
        {
            DependencyError = ex.Message;
        }

        await RefreshAsync();
        _loading = true;
        DependencyKey = PickerOption.NoneKey;
        _loading = false;
    }

    // ---- Comments ----

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        try
        {
            if (await RunAsync(() => _s.Board.AddCommentAsync(Id, NewComment), "Could not add the comment."))
            {
                NewComment = null;
                _s.Session.Announce(Id, this);
                await RefreshAsync();
            }
        }
        catch (DomainValidationException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
        }
    }

    // ---- Time tracking link ----

    partial void OnTaskKeyChanged(int value)
    {
        if (_loading || Detail is null || value == (Detail.Summary.WorkTaskId ?? PickerOption.NoneKey))
        {
            return;
        }

        _ = SetTaskAsync(PickerOption.FromKey(value));
    }

    private async Task SetTaskAsync(int? taskId)
    {
        await RunAsync(async () => _s.Session.Record(await _s.Board.SetWorkTaskAsync(Id, taskId), this), "Could not link the task.");
        await RefreshAsync();
    }

    /// <summary>Creates a task with the card's title for the chosen client and links it (no duplicate records).</summary>
    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        if (PickerOption.FromKey(ClientKey) is not { } clientId)
        {
            Notifications.Show("Choose a stakeholder for the new task.", NotificationKind.Warning);
            return;
        }

        try
        {
            await RunAsync(async () =>
            {
                var task = await _s.Tasks.CreateAsync(new TaskInput(clientId, null, Key, Title, null, null, ShowOnBoard: false));
                _s.Session.Record(await _s.Board.SetWorkTaskAsync(Id, task.Id), this);
                await LoadPickersAsync();
                Notifications.Show($"Task created for {Key}. Pick it in check-ins to track time.", NotificationKind.Success);
            }, "Could not create the task.");
        }
        catch (DomainValidationException ex)
        {
            Notifications.Show(ex.Message, NotificationKind.Warning);
        }

        await RefreshAsync();
    }

    // ---- Attachments ----

    /// <summary>Adds pasted or dropped image bytes (or a file's bytes) to the card.</summary>
    public async Task AddAttachmentAsync(string name, string contentType, byte[] bytes)
    {
        if (await RunAsync(() => _s.Board.AddAttachmentAsync(Id, name, contentType, bytes), "Could not add the file."))
        {
            _s.Session.Announce(Id, this);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task BrowseAttachmentsAsync()
    {
        var files = _s.Files.OpenFiles("Attach screenshots or files", "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|All files|*.*");
        foreach (var file in files)
        {
            await AddFileAsync(file);
        }
    }

    public async Task AddFileAsync(string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > 25L * 1024 * 1024)
            {
                Notifications.Show($"{info.Name} is larger than 25 MB.", NotificationKind.Warning);
                return;
            }

            bytes = await File.ReadAllBytesAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notifications.Show($"{Path.GetFileName(path)} could not be read: {ex.Message}", NotificationKind.Warning);
            return;
        }

        await AddAttachmentAsync(Path.GetFileName(path), "", bytes);
    }

    [RelayCommand]
    private async Task SaveCaptionAsync(AttachmentItemViewModel attachment)
    {
        if (attachment.Caption == attachment.Item.Caption)
        {
            return;
        }

        await RunAsync(() => _s.Board.SetAttachmentCaptionAsync(attachment.Id, attachment.Caption), "Could not save the caption.");
    }

    [RelayCommand]
    private async Task RemoveAttachmentAsync(AttachmentItemViewModel attachment)
    {
        if (!_s.Dialogs.Confirm("Remove screenshot", $"Remove {attachment.Name} from {Key}? The file is deleted from this PC and this cannot be undone.", "Remove", destructive: true))
        {
            return;
        }

        if (await RunAsync(() => _s.Board.RemoveAttachmentAsync(attachment.Id), "Could not remove the file."))
        {
            _s.Session.Announce(Id, this);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private void OpenAttachment(AttachmentItemViewModel attachment) => _s.Files.OpenFile(attachment.FullPath);

    // ---- Sync ----

    private void OnBoardChanged(object? sender, BoardChange change)
    {
        if (!_disposed && !ReferenceEquals(change.Source, this) && (change.CardId is null || change.CardId == Id))
        {
            _ = RefreshAsync();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _s.Session.Changed -= OnBoardChanged;
        _saveCts?.Cancel();
        _saveCts?.Dispose();
    }
}

/// <summary>A screenshot or file on the open card.</summary>
public sealed partial class AttachmentItemViewModel(AttachmentItem item) : ObservableObject
{
    public AttachmentItem Item { get; } = item;

    public int Id => Item.Id;

    public string Name => Item.OriginalName;

    public string FullPath => Item.FullPath;

    public bool IsImage => Item.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(Item.FullPath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    public string SizeText => Item.SizeBytes < 1024 * 1024 ? $"{Math.Max(1, Item.SizeBytes / 1024)} KB" : $"{Item.SizeBytes / (1024.0 * 1024):0.0} MB";

    [ObservableProperty]
    public partial string Caption { get; set; } = item.Caption;
}
