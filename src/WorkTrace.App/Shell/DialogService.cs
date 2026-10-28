using System.Windows;
using WorkTrace.App.Views;

namespace WorkTrace.App.Shell;

public interface IDialogService
{
    /// <summary>Modal confirmation. Returns true when the user confirms.</summary>
    bool Confirm(string title, string message, string confirmText, bool destructive = false);
}

internal sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, string confirmText, bool destructive = false)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current.MainWindow;
        var dialog = new ConfirmDialog(title, message, confirmText, destructive);
        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        return dialog.ShowDialog() == true;
    }
}
