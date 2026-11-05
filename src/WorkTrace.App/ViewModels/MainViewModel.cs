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
        TimesheetViewModel timesheet,
        ScreenshotsViewModel screenshots,
        INavigationService navigation,
        NotificationService notifications,
        WorkTrace.Core.Settings.ISettingsStore settingsStore)
    {
        Notifications = notifications;
        _settings = settingsStore;
        Sections =
        [
            new(AppSection.Home, "Overview", "", "Ctrl+1", home),
            new(AppSection.Timesheet, "Timesheet", "\uE787", "Ctrl+8", timesheet),
            new(AppSection.Tasks, "Tasks", "", "Ctrl+2", tasks),
            new(AppSection.Clients, "Stakeholders", "", "Ctrl+3", clients),
            new(AppSection.Lists, "Lists", "", "Ctrl+4", lists),
            new(AppSection.Board, "Board", "\uE8A9", "Ctrl+6", board),
            new(AppSection.Guides, "Guides", "\uE82D", "Ctrl+7", guides),
            new(AppSection.Screenshots, "Screenshots", "\uE722", "Ctrl+9", screenshots),
            new(AppSection.Settings, "Settings", "\uE713", "Ctrl+5", settings),
        ];
        navigation.NavigationRequested += (_, request) => NavigateTo(request.Section, request.Parameter);
        UpdateVisibleSections();
        settingsStore.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(UpdateVisibleSections);
    }

    private readonly WorkTrace.Core.Settings.ISettingsStore _settings;

    public IReadOnlyList<NavigationItem> Sections { get; }

    /// <summary>The navigation rail: every section, except the Board while it is switched off.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavigationItem> VisibleSections { get; } = [];

    private bool IsVisible(AppSection section) => section != AppSection.Board || _settings.Current.Board.IsEnabled;

    private void UpdateVisibleSections()
    {
        var wanted = Sections.Where(s => IsVisible(s.Section)).ToList();
        if (wanted.SequenceEqual(VisibleSections))
        {
            return;
        }

        var selected = SelectedSection;
        VisibleSections.Clear();
        foreach (var section in wanted)
        {
            VisibleSections.Add(section);
        }

        if (selected is not null && !wanted.Contains(selected))
        {
            NavigateTo(AppSection.Home);
        }
        else
        {
            SelectedSection = selected;
        }
    }

    public NotificationService Notifications { get; }

    public string VersionText { get; } = $"WorkTrace {AppVersion.Text}";

    [ObservableProperty]
    public partial NavigationItem? SelectedSection { get; set; }

    public void NavigateTo(AppSection section, object? parameter = null)
    {
        if (!IsVisible(section))
        {
            section = AppSection.Home; // the Board is switched off
        }

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
