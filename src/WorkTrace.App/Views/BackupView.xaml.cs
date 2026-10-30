using System.Windows.Controls;

namespace WorkTrace.App.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
    }

    /// <summary>The key goes straight from the password box to Credential Manager; it is never bound or kept.</summary>
    private void OnSaveKey(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.BackupViewModel vm)
        {
            vm.SaveEmailKey(KeyBox.Password);
            KeyBox.Clear();
        }
    }
}
