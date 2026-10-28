using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// One configurable list (task systems or communication channels) with inline rename,
/// show/hide, reorder, add and delete. Every change is saved immediately.
/// </summary>
public sealed partial class LookupListViewModel(
    LookupKind kind,
    string title,
    string description,
    ILookupService lookups,
    IDialogService dialogs,
    INotificationService notifications,
    ILogger logger) : ViewModelBase(notifications, logger)
{
    public LookupKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string Description { get; } = description;

    public ObservableCollection<LookupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial string? NewName { get; set; }

    [ObservableProperty]
    public partial string? NewNameError { get; private set; }

    partial void OnNewNameChanged(string? value) => NewNameError = null;

    public async Task LoadAsync(int? focusId = null)
    {
        IReadOnlyList<LookupSummary> items = [];
        if (!await RunAsync(async () => items = await lookups.GetAsync(Kind), $"Could not load {Title.ToLowerInvariant()}."))
        {
            return;
        }

        Items.Clear();
        for (var i = 0; i < items.Count; i++)
        {
            Items.Add(new LookupItemViewModel(this, items[i], isFirst: i == 0, isLast: i == items.Count - 1));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private async Task AddAsync()
    {
        try
        {
            if (await RunAsync(() => lookups.CreateAsync(Kind, NewName), "Could not add the item."))
            {
                NewName = null;
                await LoadAsync();
            }
        }
        catch (DomainValidationException ex)
        {
            NewNameError = ex.Errors.For("Name");
        }
    }

    private bool CanAdd() => Text.Clean(NewName) is not null;

    internal async Task RenameAsync(LookupItemViewModel item, string? name)
    {
        try
        {
            if (await RunAsync(() => lookups.RenameAsync(Kind, item.Id, name), "Could not rename the item.", () => LoadAsync()))
            {
                await LoadAsync();
            }
        }
        catch (DomainValidationException ex)
        {
            item.ShowRenameError(ex.Errors.For("Name"));
        }
    }

    internal async Task SetActiveAsync(LookupItemViewModel item, bool isActive)
    {
        if (await RunAsync(() => lookups.SetActiveAsync(Kind, item.Id, isActive), "Could not update the item.", () => LoadAsync()))
        {
            Notifications.Show(isActive ? $"“{item.Name}” is shown in pickers again." : $"“{item.Name}” is hidden from pickers. Existing records keep it.");
        }

        await LoadAsync();
    }

    internal async Task MoveAsync(LookupItemViewModel item, int direction)
    {
        await RunAsync(() => lookups.MoveAsync(Kind, item.Id, direction), "Could not reorder the list.");
        await LoadAsync();
    }

    internal async Task DeleteAsync(LookupItemViewModel item)
    {
        if (dialogs.Confirm("Delete item", $"Delete “{item.Name}”? This can't be undone.", "Delete", destructive: true)
            && await RunAsync(() => lookups.DeleteAsync(Kind, item.Id), "Could not delete the item.", () => LoadAsync()))
        {
            Notifications.Show($"Deleted “{item.Name}”.");
            await LoadAsync();
        }
    }
}
