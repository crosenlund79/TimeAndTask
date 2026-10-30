using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

public partial class ScreenshotsView : UserControl
{
    public ScreenshotsView()
    {
        InitializeComponent();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ScreenshotsViewModel vm)
        {
            vm.SetSelection(ShotList.SelectedItems.OfType<ScreenshotItemViewModel>());
        }
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        ShotList.SelectAll();
        ShotList.Focus();
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: ScreenshotItemViewModel item } && DataContext is ScreenshotsViewModel vm)
        {
            vm.OpenCommand.Execute(item);
        }
    }
}
