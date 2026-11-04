using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

public partial class HomeView : UserControl
{
    public HomeView() => InitializeComponent();

    // Browsing the lookup must not record anything: the choice counts once the list closes or focus leaves it.
    private void OnCurrentTaskClosed(object? sender, EventArgs e) => Commit();

    private void OnCurrentTaskLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!CurrentTaskBox.IsDropDownOpen && !CurrentTaskBox.IsKeyboardFocusWithin)
        {
            Commit();
        }
    }

    private void Commit()
    {
        if (DataContext is HomeViewModel vm && vm.CommitCurrentTaskCommand.CanExecute(null))
        {
            vm.CommitCurrentTaskCommand.Execute(null);
        }
    }
}
