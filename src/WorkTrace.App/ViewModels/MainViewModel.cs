using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkTrace.App.Shell;

namespace WorkTrace.App.ViewModels;

public sealed record NavigationItem(AppSection Section, string Title, string Glyph, string Shortcut, ISectionViewModel Page);

/// <summary>Main window: navigation between sections and the shared notification area.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private object? _pendingParameter;

    public MainViewModel(
        HomeViewModel home,
        TasksViewModel tasks,
        ClientsViewModel clients,
        ListsViewModel lists,
        SettingsViewModel settings,
        BoardViewModel board,
        GuidesViewModel guides,
        INavigationService navigation,
        NotificationService notifications)
    {
        Notifications = notifications;
        Sections =
        [
            new(AppSection.Home, "Overview", "", "Ctrl+1", home),
            new(AppSection.Tasks, "Tasks", "", "Ctrl+2", tasks),
            new(AppSection.Clients, "Clients", "", "Ctrl+3", clients),
            new(AppSection.Lists, "Lists", "", "Ctrl+4", lists),
            new(AppSection.Board, "Board", "\uE8A9", "Ctrl+6", board),
            new(AppSection.Guides, "Guides", "\uE82D", "Ctrl+7", guides),
            new(AppSection.Settings, "Settings", "\uE713", "Ctrl+5", settings),
        ];
        navigation.NavigationRequested += (_, request) => NavigateTo(request.Section, request.Parameter);
    }

    public IReadOnlyList<NavigationItem> Sections { get; }

    public NotificationService Notifications { get; }

    [ObservableProperty]
    public partial NavigationItem? SelectedSection { get; set; }

    public void NavigateTo(AppSection section, object? parameter = null)
    {
        var target = Sections.First(s => s.Section == section);
        if (ReferenceEquals(target, SelectedSection))
        {
            _ = target.Page.ActivateAsync(parameter);
            return;
        }

        _pendingParameter = parameter;
        SelectedSection = target;
    }

    [RelayCommand]
    private void GoToSection(AppSection section) => NavigateTo(section);

    /// <summary>Ctrl+Shift+C anywhere in the window: Tasks › Inbox, cursor in the capture box.</summary>
    [RelayCommand]
    private void QuickCapture() => NavigateTo(AppSection.Tasks, new CaptureRequest());

    partial void OnSelectedSectionChanging(NavigationItem? oldValue, NavigationItem? newValue)
    {
        if (oldValue?.Page.HasUnsavedChanges == true && newValue is not null && !ReferenceEquals(oldValue, newValue))
        {
            Notifications.Show("Unsaved changes are kept on the previous page until you save or discard them.", NotificationKind.Info);
        }
    }

    partial void OnSelectedSectionChanged(NavigationItem? value)
    {
        var parameter = _pendingParameter;
        _pendingParameter = null;
        if (value is not null)
        {
            _ = value.Page.ActivateAsync(parameter);
        }
    }
}
