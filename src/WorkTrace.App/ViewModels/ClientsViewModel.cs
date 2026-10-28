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

/// <summary>Client management page. Same list/editor pattern as <see cref="TasksViewModel"/>.</summary>
public sealed partial class ClientsViewModel(
    IClientService clients,
    IDialogService dialogs,
    INavigationService navigation,
    INotificationService notifications,
    TimeProvider timeProvider,
    ILogger<ClientsViewModel> logger) : ViewModelBase(notifications, logger), ISectionViewModel
{
    private IReadOnlyList<ClientSummary> _all = [];

    public event EventHandler? EditorFocusRequested;

    public ObservableCollection<ClientItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string? FilterText { get; set; }

    [ObservableProperty]
    public partial bool ShowInactive { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleActiveCommand), nameof(DeleteCommand), nameof(ShowTasksCommand))]
    public partial ClientItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditor), nameof(IsListEnabled), nameof(HasUnsavedChanges))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(ToggleActiveCommand), nameof(DeleteCommand), nameof(ShowTasksCommand))]
    public partial ClientEditorViewModel? Editor { get; private set; }

    [ObservableProperty]
    public partial string? EmptyMessage { get; private set; }

    public bool HasEditor => Editor is not null;

    public bool HasUnsavedChanges => Editor?.IsDirty == true;

    public bool IsListEnabled => !HasUnsavedChanges;

    public async Task ActivateAsync(object? parameter)
    {
        await ReloadAsync(Editor?.Id ?? SelectedItem?.Id);
        if (parameter is CreateNewRequest)
        {
            NewClient();
        }
    }

    partial void OnFilterTextChanged(string? value) => ApplyFilter(SelectedItem?.Id);

    partial void OnShowInactiveChanged(bool value) => ApplyFilter(SelectedItem?.Id);

    partial void OnSelectedItemChanged(ClientItemViewModel? value)
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
        }
        else if (Editor?.Id != value.Id)
        {
            SetEditor(new ClientEditorViewModel(value.Summary));
        }
    }

    [RelayCommand]
    private void NewClient()
    {
        if (!HasUnsavedChanges)
        {
            SelectedItem = null;
            SetEditor(new ClientEditorViewModel(null));
        }

        EditorFocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        ClientSummary? saved = null;
        try
        {
            if (!await RunAsync(
                    async () => saved = editor.IsNew ? await clients.CreateAsync(editor.ToInput()) : await clients.UpdateAsync(editor.Id!.Value, editor.ToInput()),
                    "Could not save the client.",
                    () => ReloadAsync(null)))
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
        await ReloadAsync(saved!.Id);
        Notifications.Show(editor.IsNew ? $"Client “{saved.Name}” created." : "Changes saved.", NotificationKind.Success);
    }

    private bool CanSave() => Editor?.IsDirty == true;

    [RelayCommand(CanExecute = nameof(CanDiscard))]
    private void Discard() => SetEditor(SelectedItem is { } item ? new ClientEditorViewModel(item.Summary) : null);

    private bool CanDiscard() => Editor is { } e && (e.IsDirty || e.IsNew);

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private async Task ToggleActiveAsync()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var activate = !item.IsActive;
        if (await RunAsync(() => clients.SetActiveAsync(item.Id, activate), "Could not update the client.", () => ReloadAsync(null)))
        {
            SetEditor(null);
            await ReloadAsync(item.Id);
            Notifications.Show(
                activate ? $"“{item.Name}” is active again." : $"Deactivated “{item.Name}”. Its tasks are hidden from pickers but kept for history.",
                NotificationKind.Info,
                "Undo",
                async () =>
                {
                    if (await RunAsync(() => clients.SetActiveAsync(item.Id, !activate), "Could not undo."))
                    {
                        SetEditor(null);
                        await ReloadAsync(item.Id);
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
        if (!await RunAsync(async () => canDelete = await clients.CanDeleteAsync(item.Id), "Could not check the client."))
        {
            return;
        }

        if (!canDelete)
        {
            if (item.IsActive && dialogs.Confirm(
                    "Client has tasks",
                    $"“{item.Name}” has tasks and is kept for history. Deactivate it instead? It will be hidden from pickers and can be reactivated at any time.",
                    "Deactivate"))
            {
                await ToggleActiveAsync();
            }

            return;
        }

        if (dialogs.Confirm("Delete client", $"Delete “{item.Name}” permanently? This can't be undone.", "Delete", destructive: true)
            && await RunAsync(() => clients.DeleteAsync(item.Id), "Could not delete the client.", () => ReloadAsync(null)))
        {
            SetEditor(null);
            await ReloadAsync(null);
            Notifications.Show($"Deleted “{item.Name}”.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private void ShowTasks()
    {
        if (SelectedItem is { } item)
        {
            navigation.Navigate(AppSection.Tasks, new ClientTasksFilter(item.Id));
        }
    }

    private bool CanModifySelected() => SelectedItem is not null && !HasUnsavedChanges;

    private async Task ReloadAsync(int? selectId)
    {
        if (await RunAsync(async () => _all = await clients.GetClientsAsync(includeInactive: true), "Could not load clients."))
        {
            ApplyFilter(selectId, refreshEditor: true);
        }
    }

    private void ApplyFilter(int? selectId, bool refreshEditor = false)
    {
        var now = UtcTime.Now(timeProvider);
        var filter = Text.Clean(FilterText);
        var visible = _all
            .Where(c => ShowInactive || c.IsActive || c.Id == selectId)
            .Where(c => filter is null || Text.IndexOf(c.Name, filter) >= 0 || Text.IndexOf(c.Description, filter) >= 0)
            .OrderByDescending(c => c.IsActive)
            .ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase);

        Items.Clear();
        foreach (var client in visible)
        {
            Items.Add(new ClientItemViewModel(client, RelativeTime.Format(client.LastUsedAtUtc, now, TimeZoneInfo.Local)));
        }

        EmptyMessage = Items.Count > 0 ? null : filter is null ? "No clients yet. Press Ctrl+N to add one." : "No clients match your filter.";

        var target = Items.FirstOrDefault(i => i.Id == selectId);
        if (refreshEditor && !HasUnsavedChanges)
        {
            SetEditor(target is null ? null : new ClientEditorViewModel(target.Summary));
        }

        SelectedItem = target;
    }

    private void SetEditor(ClientEditorViewModel? editor)
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
        if (e.PropertyName == nameof(ClientEditorViewModel.IsDirty))
        {
            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(IsListEnabled));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            ToggleActiveCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            ShowTasksCommand.NotifyCanExecuteChanged();
        }
    }
}
