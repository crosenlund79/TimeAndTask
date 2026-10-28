using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>Configurable lists page: task systems and communication channels side by side.</summary>
public sealed class ListsViewModel : ISectionViewModel
{
    public ListsViewModel(ILookupService lookups, IDialogService dialogs, INotificationService notifications, ILogger<ListsViewModel> logger)
    {
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

    public LookupListViewModel TaskSystems { get; }

    public LookupListViewModel Channels { get; }

    public bool HasUnsavedChanges => false;

    public async Task ActivateAsync(object? parameter)
    {
        await TaskSystems.LoadAsync();
        await Channels.LoadAsync();
    }
}
