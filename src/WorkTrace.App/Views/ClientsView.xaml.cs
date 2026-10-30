using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

public partial class ClientsView : UserControl
{
    private ClientsViewModel? _viewModel;

    public ClientsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as ClientsViewModel);
        Loaded += (_, _) => ListNavigation.FocusSoon(SearchBox);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(ClientsViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested -= OnEditorFocusRequested;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.EditorFocusRequested += OnEditorFocusRequested;
        }
    }

    private void OnEditorFocusRequested(object? sender, EventArgs e) => ListNavigation.FocusSoon(NameBox, selectAll: true);

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down)
        {
            ListNavigation.FocusFirstItem(ClientList);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && !string.IsNullOrEmpty(SearchBox.Text))
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel?.HasEditor == true)
        {
            ListNavigation.FocusSoon(NameBox);
            e.Handled = true;
        }
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ClientsViewModel vm)
        {
            vm.SetSelection(ClientList.SelectedItems.OfType<ClientItemViewModel>());
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        ClientList.SelectAll();
        ClientList.Focus();
    }
}
