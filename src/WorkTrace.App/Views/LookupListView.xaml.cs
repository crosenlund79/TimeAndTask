using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

public partial class LookupListView : UserControl
{
    public LookupListView() => InitializeComponent();

    /// <summary>Renames commit when the inline editor loses focus, like a spreadsheet cell.</summary>
    private void OnNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LookupItemViewModel item } && item.CommitRenameCommand.CanExecute(null))
        {
            item.CommitRenameCommand.Execute(null);
        }
    }
}
