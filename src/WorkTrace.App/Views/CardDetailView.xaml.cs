using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

/// <summary>The open card. Code-behind only turns pasted and dropped images/files into bytes for the view model.</summary>
public partial class CardDetailView : UserControl
{
    public CardDetailView()
    {
        InitializeComponent();
    }

    private CardDetailViewModel? ViewModel => DataContext as CardDetailViewModel;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+V with an image on the clipboard attaches it; text pastes into text boxes as usual.
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && ViewModel is { } vm
            && ImageClipboard.TryGetPng() is { } png && !(e.OriginalSource is TextBox && Clipboard.ContainsText()))
        {
            e.Handled = true;
            _ = vm.AddAttachmentAsync($"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image/png", png);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files.Where(File.Exists))
            {
                await vm.AddFileAsync(file);
            }
        }
        else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
        {
            await vm.AddAttachmentAsync($"image-{DateTime.Now:yyyyMMdd-HHmmss}.png", "image/png", ImageClipboard.ToPng(bitmap));
        }
    }

    private void OnCaptionLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AttachmentItemViewModel attachment } && ViewModel is { } vm)
        {
            vm.SaveCaptionCommand.Execute(attachment);
        }
    }
}

/// <summary>Clipboard images as PNG bytes.</summary>
internal static class ImageClipboard
{
    public static byte[]? TryGetPng()
    {
        try
        {
            return Clipboard.ContainsImage() && Clipboard.GetImage() is { } image ? ToPng(image) : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // clipboard busy
        }
    }

    public static byte[] ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
