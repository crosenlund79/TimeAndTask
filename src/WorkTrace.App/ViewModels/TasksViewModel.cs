using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// Task management page: search-as-you-type list on the left, edit form on the right.
/// While the form has unsaved changes the list is locked, so edits are never lost silently.
/// </summary>
public sealed partial class TasksViewModel : ViewModelBase, ISectionViewModel, IDisposable
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan SimilarCheckDelay = TimeSpan.FromMilliseconds(350);

    private readonly ITaskService _tasks;
    private readonly IClientService _clients;
    private readonly ILookupService _lookups;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _timeProvider;

    private IReadOnlyList<PickerOption> _clientOptions = [];
    private IReadOnlyList<PickerOption> _systemOptions = [];
    private IReadOnlyList<PickerOption> _channelOptions = [];
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _similarCts;
    private bool _suppressReload;

    public TasksViewModel(
        ITaskService tasks,
        IClientService clients,
        ILookupService lookups,
        IDialogService dialogs,
        INotificationService notifications,
        TimeProvider timeProvider,
        ILogger<TasksViewModel> logger)
        : base(notifications, logger)
    {
        _tasks = tasks;
        _clients = clients;
        _lookups = lookups;
        _dialogs = dialogs;
        _timeProvider = timeProvider;
    }

    /// <summary>Raised when the view should move keyboard focus into the editor (new task).</summary>
    public event EventHandler? EditorFocusRequested;

    public ObservableCollection<TaskItemViewModel> Items { get; } = [];

    public ObservableCollection<PickerOption> ClientFilterOptions { get; } = [];

    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>Selected client filter key (0 = all clients; null while the option list is rebuilt).</summary>
    [ObservableProperty]
    public partial int? ClientFilterKey { get; set; } = PickerOption.NoneKey;

    public int? ClientFilterId => ClientFilterKey is { } key ? PickerOption.FromKey(key) : null;

    [ObservableProperty]
    public partial bool ShowArchived { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleArchiveCommand), nameof(DeleteCommand))]
    public partial TaskItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(IsListEnabled), nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(ToggleArchiveCommand), nameof(DeleteCommand))]
    public partial TaskEditorViewModel? Editor { get; private set; }

    [ObservableProperty]
    public partial string? EmptyMessage { get; private set; }

    [ObservableProperty]
    public partial string? ResultSummary { get; private set; }

    public bool HasEditor => Editor is not null;

    public bool HasUnsavedChanges => Editor?.IsDirty == true;

    public bool IsListEnabled => !HasUnsavedChanges;

    public async Task ActivateAsync(object? parameter)
    {
        await RunAsync(LoadPickersAsync, "Could not load clients and lists.");

        _suppressReload = true;
        if (parameter is ClientTasksFilter filter)
        {
            ClientFilterKey = filter.ClientId;
            SearchText = null;
        }

        _suppressReload = false;
        await ReloadAsync(selectId: Editor?.Id ?? SelectedItem?.Id);

        if (parameter is CreateNewRequest)
        {
            NewTask();
        }
    }

    partial void OnSearchTextChanged(string? value) => ScheduleReload();

    partial void OnClientFilterKeyChanged(int? value) => ScheduleReload();

    partial void OnShowArchivedChanged(bool value) => ScheduleReload();

    partial void OnSelectedItemChanged(TaskItemViewModel? value)
    {
        if (HasUnsavedChanges)
        {
            return;
        }

        if (value is null)
        {
            if (Editor is { IsNew: false })
            {
                SetEditor(null);
            }

            return;
        }

        if (Editor?.Id != value.Id)
        {
            SetEditor(CreateEditor(value.Summary));
        }
    }

    [RelayCommand]
    private void NewTask()
    {
        if (HasUnsavedChanges)
        {
            EditorFocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var defaultClient = ClientFilterId
            ?? SelectedItem?.Summary.ClientId
            ?? _clientOptions.FirstOrDefault(c => c.IsActive)?.Id;
        SelectedItem = null;
        SetEditor(CreateEditor(null, defaultClient));
        EditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        TaskSummary? saved = null;
        try
        {
            var ok = await RunAsync(async () =>
            {
                saved = editor.IsNew
                    ? await _tasks.CreateAsync(editor.ToInput())
                    : await _tasks.UpdateAsync(editor.Id!.Value, editor.ToInput());
            }, "Could not save the task.", onNotFound: () => ReloadAsync(null));
            if (!ok || saved is null)
            {
                return;
            }
        }
        catch (DomainValidationException ex)
        {
            editor.ApplyErrors(ex.Errors);
            return;
        }

        SetEditor(null);
        await ReloadAsync(saved.Id, forceEditor: true);
        Notifications.Show(editor.IsNew ? $"Task “{saved.Title}” created." : "Changes saved.", NotificationKind.Success);
    }

    private bool CanSave() => Editor?.IsDirty == true;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard()
    {
        if (Editor is null)
        {
            return;
        }

        SetEditor(SelectedItem is { } item ? CreateEditor(item.Summary) : null);
    }

    private bool CanDiscard() => Editor is { } e && (e.IsDirty || e.IsNew);

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task ToggleArchiveAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var archive = !item.IsArchived;
        if (await RunAsync(() => _tasks.SetArchivedAsync(item.Id, archive), "Could not update the task.", () => ReloadAsync(null)))
        {
            await ReloadAsync(item.Id, forceEditor: true);
            Notifications.Show(
                archive ? $"Archived “{item.Title}”." : $"Restored “{item.Title}”.",
                NotificationKind.Info,
                "Undo",
                async () =>
                {
                    if (await RunAsync(() => _tasks.SetArchivedAsync(item.Id, !archive), "Could not undo."))
                    {
                        await ReloadAsync(item.Id, forceEditor: true);
                    }
                });
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var canDelete = false;
        if (!await RunAsync(async () => canDelete = await _tasks.CanDeleteAsync(item.Id), "Could not check the task."))
        {
            return;
        }

        if (!canDelete)
        {
            if (!item.IsArchived && _dialogs.Confirm(
                    "Task has recorded time",
                    $"“{item.Title}” is used by recorded time and can't be deleted. Archive it instead? Archived tasks keep their history and can be restored at any time.",
                    "Archive"))
            {
                await ToggleArchiveAsync();
            }

            return;
        }

        if (_dialogs.Confirm("Delete task", $"Delete “{item.Title}” permanently? This can't be undone.", "Delete", destructive: true)
            && await RunAsync(() => _tasks.DeleteAsync(item.Id), "Could not delete the task.", () => ReloadAsync(null)))
        {
            SetEditor(null);
            await ReloadAsync(null);
            Notifications.Show($"Deleted “{item.Title}”.");
        }
    }

    private bool CanModifySelected() => SelectedItem is not null && !HasUnsavedChanges;

    [RelayCommand]
    private async Task CreateClientFromTextAsync()
    {
        if (Editor is not { CanCreateClient: true } editor)
        {
            return;
        }

        ClientSummary? created = null;
        try
        {
            if (!await RunAsync(async () => created = await _clients.CreateAsync(new ClientInput(editor.ClientText, null, false)), "Could not create the client."))
            {
                return;
            }
        }
        catch (DomainValidationException ex)
        {
            editor.ApplyErrors(new ValidationErrors());
            Notifications.Show(ex.Message, NotificationKind.Warning);
            return;
        }

        // Rebuild the editor with the new client available, preserving everything typed so far.
        await RunAsync(LoadPickersAsync, "Could not reload clients.");
        var input = editor.ToInput();
        var rebuilt = CreateEditor(editor.Existing, created!.Id);
        rebuilt.ClientId = created.Id;
        rebuilt.TaskSystemId = input.TaskSystemId;
        rebuilt.ExternalId = input.ExternalId;
        rebuilt.Title = input.Title;
        rebuilt.Notes = input.Notes;
        rebuilt.DefaultChannelId = input.DefaultChannelId;
        SetEditor(rebuilt);
        Notifications.Show($"Client “{created.Name}” created.", NotificationKind.Success);
    }

    private async Task LoadPickersAsync()
    {
        var clients = await _clients.GetClientsAsync(includeInactive: true);
        var systems = await _lookups.GetAsync(LookupKind.TaskSystem);
        var channels = await _lookups.GetAsync(LookupKind.CommunicationChannel);

        // Recently used clients first: the most likely choice is at the top of the list.
        _clientOptions = [.. clients
            .OrderByDescending(c => c.IsActive)
            .ThenByDescending(c => c.LastUsedAtUtc ?? DateTime.MinValue)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new PickerOption(c.Id, c.Name, c.IsActive))];
        _systemOptions = [.. systems.Select(s => new PickerOption(s.Id, s.Name, s.IsActive))];
        _channelOptions = [.. channels.Select(c => new PickerOption(c.Id, c.Name, c.IsActive))];

        var filter = ClientFilterKey;
        _suppressReload = true;
        ClientFilterOptions.Clear();
        ClientFilterOptions.Add(new PickerOption(PickerOption.NoneKey, "All clients"));
        foreach (var client in clients.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ClientFilterOptions.Add(new PickerOption(client.Id, client.Name, client.IsActive));
        }

        ClientFilterKey = ClientFilterOptions.Any(o => o.Id == filter) ? filter : PickerOption.NoneKey;
        _suppressReload = false;
    }

    private void ScheduleReload()
    {
        if (_suppressReload)
        {
            return;
        }

        _ = DelayedReloadAsync(Restart(ref _searchCts));
    }

    private async Task DelayedReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SearchDelay, cancellationToken);
            await ReloadAsync(SelectedItem?.Id, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReloadAsync(int? selectId, bool forceEditor = false, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TaskSummary> results = [];
        var query = new TaskQuery(SearchText, ClientFilterId, IncludeArchived: ShowArchived, IncludeInactiveClients: ClientFilterId is not null);
        if (!await RunAsync(async () => results = await _tasks.SearchAsync(query, cancellationToken), "Could not load tasks."))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var now = UtcTime.Now(_timeProvider);
        Items.Clear();
        foreach (var task in results)
        {
            Items.Add(new TaskItemViewModel(task, RelativeTime.Format(task.LastUsedAtUtc, now, TimeZoneInfo.Local, never: "not used yet")));
        }

        var hasQuery = !string.IsNullOrWhiteSpace(SearchText) || ClientFilterId is not null;
        EmptyMessage = Items.Count > 0 ? null
            : hasQuery ? "No tasks match your search."
            : "No tasks yet. Press Ctrl+N to add your first task.";
        ResultSummary = Items.Count switch
        {
            0 => null,
            1 => "1 task",
            >= 200 => "Showing the 200 best matches",
            _ => $"{Items.Count} tasks",
        };

        if (forceEditor && !HasUnsavedChanges)
        {
            SetEditor(null);
        }

        var target = Items.FirstOrDefault(i => i.Id == selectId);
        if (!ReferenceEquals(SelectedItem, target))
        {
            SelectedItem = target;
        }
        else if (forceEditor && target is not null && !HasUnsavedChanges)
        {
            SetEditor(CreateEditor(target.Summary));
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _similarCts?.Cancel();
        _similarCts?.Dispose();
    }

    /// <summary>Cancels the previous debounced operation and returns a token for the next one.</summary>
    private static CancellationToken Restart(ref CancellationTokenSource? cts)
    {
        cts?.Cancel();
        cts?.Dispose();
        cts = new CancellationTokenSource();
        return cts.Token;
    }

    private TaskEditorViewModel CreateEditor(TaskSummary? existing, int? defaultClientId = null) =>
        new(existing, defaultClientId, _clientOptions, _systemOptions, _channelOptions);

    private void SetEditor(TaskEditorViewModel? editor)
    {
        if (Editor is not null)
        {
            Editor.PropertyChanged -= OnEditorPropertyChanged;
        }

        Editor = editor;
        if (editor is not null)
        {
            editor.PropertyChanged += OnEditorPropertyChanged;
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TaskEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(IsListEnabled));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ToggleArchiveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
        }

        if (e.PropertyName is nameof(TaskEditorViewModel.ExternalId) or nameof(TaskEditorViewModel.ClientId) or nameof(TaskEditorViewModel.TaskSystemId)
            && sender is TaskEditorViewModel editor)
        {
            _ = CheckSimilarAsync(editor, Restart(ref _similarCts));
        }
    }

    private async Task CheckSimilarAsync(TaskEditorViewModel editor, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SimilarCheckDelay, cancellationToken);
            var similar = await _tasks.FindSimilarAsync(editor.ToInput(), editor.Id, cancellationToken);
            editor.SimilarWarning = similar.Count == 0
                ? null
                : $"Same ID also used by “{similar[0].Title}”" + (similar.Count > 1 ? $" and {similar.Count - 1} more" : string.Empty) + ". That's allowed — just checking.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Similar-task check failed");
        }
    }
}
