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

    [ObservableProperty]
    public partial string? ResultSummary { get; private set; }

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
                    "Could not save the stakeholder.",
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
        Notifications.Show(editor.IsNew ? $"Stakeholder “{saved.Name}” created." : "Changes saved.", NotificationKind.Success);
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
        if (await RunAsync(() => clients.SetActiveAsync(item.Id, activate), "Could not update the stakeholder.", () => ReloadAsync(null)))
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

    /// <summary>All selected rows (Ctrl/Shift multi-selection); set by the view.</summary>
    public IReadOnlyList<ClientItemViewModel> SelectedItems { get; private set; } = [];

    [ObservableProperty]
    public partial int SelectionCount { get; private set; }

    public void SetSelection(IEnumerable<ClientItemViewModel> items)
    {
        SelectedItems = [.. items];
        SelectionCount = SelectedItems.Count;
    }

    [RelayCommand(CanExecute = nameof(CanModifySelected))]
    private Task DeleteAsync() => SelectedItem is { } item ? DeleteManyAsync([item]) : Task.CompletedTask;

    /// <summary>
    /// Deletes the selected stakeholders. One whose tasks have no recorded time is deleted together with those tasks;
    /// one with recorded time can't be deleted and is deactivated instead. The confirmation lists both cases.
    /// </summary>
    [RelayCommand]
    private Task DeleteSelectedAsync() =>
        DeleteManyAsync(SelectedItems.Count > 0 ? SelectedItems : SelectedItem is { } one ? [one] : []);

    private async Task DeleteManyAsync(IReadOnlyList<ClientItemViewModel> items)
    {
        if (items.Count == 0 || HasUnsavedChanges)
        {
            return;
        }

        var usage = new Dictionary<ClientItemViewModel, StakeholderUsage>();
        if (!await RunAsync(async () =>
            {
                foreach (var item in items)
                {
                    usage[item] = await clients.GetUsageAsync(item.Id);
                }
            }, "Could not check the stakeholders."))
        {
            return;
        }

        var empty = items.Where(i => usage[i].Tasks == 0).ToList();
        var withUnusedTasks = items.Where(i => usage[i].Tasks > 0 && usage[i].CanDeleteWithTasks).ToList();
        var used = items.Where(i => !usage[i].CanDeleteWithTasks).ToList();
        var toDeactivate = used.Where(i => i.IsActive).ToList();

        var name = items.Count == 1 ? $"“{items[0].Name}”" : $"{items.Count} stakeholders";
        var lines = new List<string>();
        if (empty.Count > 0)
        {
            lines.Add(items.Count == 1 ? $"{name} will be deleted permanently." : $"{empty.Count} will be deleted permanently.");
        }

        if (withUnusedTasks.Count > 0)
        {
            lines.Add($"{(items.Count == 1 ? name : withUnusedTasks.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))} will be deleted together with "
                + $"{withUnusedTasks.Sum(i => usage[i].Tasks)} task(s) that have no recorded time.");
        }

        if (used.Count > 0)
        {
            lines.Add($"{(items.Count == 1 ? name : used.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))} "
                + $"{(items.Count == 1 ? "has" : "have")} tasks with recorded time and can't be deleted"
                + (toDeactivate.Count > 0 ? ": deactivated instead (hidden from pickers; history kept)." : " (already inactive)."));
        }

        var deleting = empty.Count + withUnusedTasks.Count;
        if (deleting + toDeactivate.Count == 0)
        {
            Notifications.Show(string.Join(" ", lines), NotificationKind.Info);
            return;
        }

        if (!dialogs.Confirm(items.Count == 1 ? "Delete stakeholder" : $"Delete {items.Count} stakeholders", string.Join("\n\n", lines),
                deleting > 0 ? "Delete" : "Deactivate", destructive: deleting > 0))
        {
            return;
        }

        await RunAsync(async () =>
        {
            foreach (var item in empty)
            {
                await clients.DeleteAsync(item.Id);
            }

            foreach (var item in withUnusedTasks)
            {
                await clients.DeleteWithUnusedTasksAsync(item.Id);
            }

            foreach (var item in toDeactivate)
            {
                await clients.SetActiveAsync(item.Id, false);
            }
        }, "Could not delete all selected stakeholders.", () => ReloadAsync(null));
        SetEditor(null);
        await ReloadAsync(null);
        Notifications.Show($"Deleted {deleting}" + (toDeactivate.Count > 0 ? $", deactivated {toDeactivate.Count} with recorded time." : "."), NotificationKind.Success);
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
        if (await RunAsync(async () => _all = await clients.GetClientsAsync(includeInactive: true), "Could not load stakeholders."))
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

        EmptyMessage = Items.Count > 0 ? null : filter is null ? "No stakeholders yet. Press Ctrl+N to add one." : "No stakeholders match your filter.";
        ResultSummary = Items.Count == _all.Count
            ? (Items.Count == 1 ? "1 stakeholder" : $"{Items.Count} stakeholders")
            : $"{Items.Count} of {_all.Count} stakeholders";

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
