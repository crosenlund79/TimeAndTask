using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class InboxView : UserControl
{
    public InboxView()
    {
        InitializeComponent();

        // Embedded in the Tasks page: take focus only when shown, never while hidden behind the task list.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Capture.FocusBox();
            }
        };
    }

    public void FocusCapture() => Capture.FocusBox();

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
