using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class GuidesView : UserControl
{
    public GuidesView()
    {
        InitializeComponent();
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);
}
