using System.Windows;

namespace WorkTrace.App.Views;

/// <summary>
/// Themed confirmation dialog. For destructive actions the safe choice (Cancel) has the initial focus
/// and Enter does not confirm by default.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string confirmText, bool destructive)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;

        if (destructive)
        {
            ConfirmButton.Foreground = (System.Windows.Media.Brush?)TryFindResource("SystemFillColorCriticalBrush") ?? ConfirmButton.Foreground;
            Loaded += (_, _) => CancelButton.Focus();
        }
        else
        {
            ConfirmButton.IsDefault = true;
            if (TryFindResource("AccentButtonStyle") is Style accent)
            {
                ConfirmButton.Style = accent;
            }

            Loaded += (_, _) => ConfirmButton.Focus();
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
