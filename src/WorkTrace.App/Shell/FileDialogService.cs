using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace WorkTrace.App.Shell;

/// <summary>File pickers and handing files or links to Windows (replaceable in tests).</summary>
public interface IFileDialogService
{
    /// <summary>Returns the chosen files, or an empty list when cancelled.</summary>
    IReadOnlyList<string> OpenFiles(string title, string filter, bool multiselect = true);

    /// <summary>Returns the chosen path, or null when cancelled.</summary>
    string? SaveFile(string title, string filter, string defaultFileName);

    /// <summary>Opens a web address in the default browser. Only http(s) addresses are opened.</summary>
    void OpenUrl(string url);

    /// <summary>Opens a local file with its default application.</summary>
    void OpenFile(string path);

    void CopyText(string text);
}

internal sealed class FileDialogService : IFileDialogService
{
    public IReadOnlyList<string> OpenFiles(string title, string filter, bool multiselect = true)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect, CheckFileExists = true };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileNames : [];
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, AddExtension = true };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public void OpenUrl(string url)
    {
        if (WorkTrace.Core.Board.BoardRules.IsWebUrl(url))
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
    }

    public void OpenFile(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void CopyText(string text) => Clipboard.SetText(text);

    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}
