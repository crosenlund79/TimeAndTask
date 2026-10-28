using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WorkTrace.App.Views;

/// <summary>Keyboard glue shared by the list pages (pure view behaviour, no business logic).</summary>
internal static class ListNavigation
{
    /// <summary>Moves focus from a search box into the list (Down arrow).</summary>
    public static void FocusFirstItem(ListBox list)
    {
        if (list.Items.Count == 0 || !list.IsEnabled)
        {
            return;
        }

        if (list.SelectedIndex < 0)
        {
            list.SelectedIndex = 0;
        }

        list.ScrollIntoView(list.SelectedItem);
        list.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem item)
            {
                item.Focus();
            }
        });
    }

    /// <summary>Focuses a control once pending layout (e.g. a newly shown editor) has completed.</summary>
    public static void FocusSoon(Control control, bool selectAll = false)
    {
        control.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            control.Focus();
            Keyboard.Focus(control);
            if (selectAll && control is TextBox box)
            {
                box.SelectAll();
            }
        });
    }
}
