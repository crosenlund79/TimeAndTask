using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class InboxView : UserControl
{
    public InboxView()
    {
        InitializeComponent();
        Loaded += (_, _) => Capture.FocusBox();
    }

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Capture.FocusBox();
            e.Handled = true;
        }
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);
}
