using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkTrace.App.Shell;

namespace WorkTrace.App.ViewModels;

public sealed record NavigationItem(AppSection Section, string Title, string Glyph, string Shortcut, ISectionViewModel Page)
{
    /// <summary>Tooltip, also the label when the rail is collapsed to icons: "Tasks (Ctrl+2)".</summary>
    public string ToolTip => $"{Title} ({Shortcut})";
}

/// <summary>Opens the Lists page on a tab (0 Stakeholders, 1 Task systems, 2 Channels), passing <see cref="Inner"/> on.</summary>
public sealed record ListsTabRequest(int Tab, object? Inner = null);

/// <summary>Opens the System page on a tab (see <see cref="SettingsViewModel"/> tab constants).</summary>
public sealed record SystemTabRequest(int Tab);

/// <summary>Main window: navigation between sections and the shared notification area.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private object? _pendingParameter;

    /// <summary>The Overview's view model (Ctrl+M toggles Mini Mode from any page).</summary>
    public HomeViewModel Home { get; }

    public MainViewModel(
        HomeViewModel home,
        TasksViewModel tasks,
        ListsViewModel lists,
        SettingsViewModel settings,
        BoardViewModel board,
        GuidesViewModel guides,
        TimesheetViewModel timesheet,
        INavigationService navigation,
        NotificationService notifications,
        WorkTrace.Core.Settings.ISettingsStore settingsStore)
    {
        Home = home;
        Notifications = notifications;
        _settings = settingsStore;
        Sections =
        [
            new(AppSection.Home, "Overview", "", "Ctrl+1", home),
            new(AppSection.Timesheet, "Timesheet", "\uE787", "Ctrl+8", timesheet),
            new(AppSection.Tasks, "Tasks", "", "Ctrl+2", tasks),
            new(AppSection.Lists, "Lists", "", "Ctrl+4", lists),
            new(AppSection.Board, "Board", "\uE8A9", "Ctrl+6", board),
            new(AppSection.Guides, "Guides", "\uE82D", "Ctrl+7", guides),
            new(AppSection.Settings, "System", "\uE713", "Ctrl+5", settings),
        ];
        navigation.NavigationRequested += (_, request) => NavigateTo(request.Section, request.Parameter);
        IsNavCollapsed = settingsStore.Current.General.NavigationCollapsed;
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

    /// <summary>Navigation rail collapsed to icons (with tooltips), giving the page the room.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NavWidth), nameof(NavToggleGlyph), nameof(NavToggleToolTip))]
    public partial bool IsNavCollapsed { get; set; }

    public System.Windows.GridLength NavWidth => new(IsNavCollapsed ? 64 : 208);

    public string NavToggleGlyph => IsNavCollapsed ? "\uE76C" : "\uE76B";

    public string NavToggleToolTip => IsNavCollapsed ? "Expand the navigation" : "Collapse the navigation to icons";

    [RelayCommand]
    private void ToggleNav()
    {
        IsNavCollapsed = !IsNavCollapsed;
        var collapsed = IsNavCollapsed;
        _ = _settings.UpdateAsync(s => s.General.NavigationCollapsed = collapsed);
    }

    public void NavigateTo(AppSection section, object? parameter = null)
    {
        // Stakeholders live on the Lists page, Screenshots on the System page.
        if (section == AppSection.Clients)
        {
            (section, parameter) = (AppSection.Lists, new ListsTabRequest(0, parameter));
        }
        else if (section == AppSection.Screenshots)
        {
            (section, parameter) = (AppSection.Settings, new SystemTabRequest(SettingsViewModel.ScreenshotsTab));
        }

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
