using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

public partial class TasksView : UserControl
{
    private TasksViewModel? _viewModel;

    public TasksView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ListNavigation.FocusSoon(SearchBox);
        Unloaded += (_, _) => Attach(null);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => Attach(e.NewValue as TasksViewModel);

    private void Attach(TasksViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested -= OnEditorFocusRequested;
            _viewModel.CaptureFocusRequested -= OnCaptureFocusRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested += OnEditorFocusRequested;
            _viewModel.CaptureFocusRequested += OnCaptureFocusRequested;
        }
    }

    private void OnCaptureFocusRequested(object? sender, EventArgs e) => InboxPanel.FocusCapture();

    private void OnEditorFocusRequested(object? sender, EventArgs e) => ListNavigation.FocusSoon(TitleBox, selectAll: true);

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            ListNavigation.FocusFirstItem(TaskList);
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel?.HasEditor == true)
        {
            ListNavigation.FocusSoon(TitleBox);
            e.Handled = true;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is TasksViewModel vm)
        {
            vm.SetSelection(TaskList.SelectedItems.OfType<TaskItemViewModel>());
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        TaskList.SelectAll();
        TaskList.Focus();
    }
}
