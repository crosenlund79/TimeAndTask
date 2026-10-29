using System.Windows.Controls;
using System.Windows.Input;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

/// <summary>Quick capture box. Pasting an image (Ctrl+V) attaches it to the next captured item.</summary>
public partial class CaptureBar : UserControl
{
    public CaptureBar()
    {
        InitializeComponent();
    }

    public void FocusBox() => ListNavigation.FocusSoon(CaptureBox, selectAll: true);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && DataContext is BoardPageViewModel vm
            && !System.Windows.Clipboard.ContainsText() && ImageClipboard.TryGetPng() is { } png)
        {
            vm.SetCaptureImage(png);
            e.Handled = true;
        }
    }
}
