using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// Maintained lists in one place, as tabs: Stakeholders (the existing stakeholder screen), task systems and
/// communication channels.
/// </summary>
public sealed partial class ListsViewModel : ObservableObject, ISectionViewModel
{
    public const int StakeholdersTab = 0;

    public ListsViewModel(ClientsViewModel clients, ILookupService lookups, IDialogService dialogs, INotificationService notifications, ILogger<ListsViewModel> logger)
    {
        Clients = clients;
        TaskSystems = new LookupListViewModel(
            LookupKind.TaskSystem,
            "Task systems",
            "Where tasks come from. Used to group the weekly timesheet.",
            lookups, dialogs, notifications, logger);
        Channels = new LookupListViewModel(
            LookupKind.CommunicationChannel,
            "Communication channels",
            "How a request reached you. Optional on tasks and check-ins.",
            lookups, dialogs, notifications, logger);
    }

    public ClientsViewModel Clients { get; }

    public LookupListViewModel TaskSystems { get; }

    public LookupListViewModel Channels { get; }

    /// <summary>0 Stakeholders, 1 Task systems, 2 Communication channels.</summary>
    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    /// <summary>Unsaved stakeholder edits keep the page (the lookup lists save immediately).</summary>
    public bool HasUnsavedChanges => Clients.HasUnsavedChanges;

    public async Task ActivateAsync(object? parameter)
    {
        object? inner = null;
        if (parameter is ListsTabRequest request)
        {
            SelectedTab = request.Tab;
            inner = request.Inner;
        }

        await Clients.ActivateAsync(inner);
        await TaskSystems.LoadAsync();
        await Channels.LoadAsync();
    }
}
