using System.Windows.Controls;
using System.Windows.Input;

namespace WorkTrace.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    /// <summary>Enter applies the custom interval (the binding otherwise updates on focus loss).</summary>
    private void OnCustomIntervalKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return)
        {
            CustomIntervalBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }
}
