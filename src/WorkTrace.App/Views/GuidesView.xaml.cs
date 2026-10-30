using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class GuidesView : UserControl
{
    public GuidesView()
    {
        InitializeComponent();
    }

    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ViewModels.GuidesViewModel vm)
        {
            vm.SetSelection(GuideList.SelectedItems.OfType<ViewModels.GuideItemViewModel>());
        }
    }

    private void OnSelectAll(object sender, System.Windows.RoutedEventArgs e)
    {
        GuideList.SelectAll();
        GuideList.Focus();
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);
}
