using System.Windows;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WorkTrace.App.Tray;

public enum TrayState
{
    /// <summary>Check-in tracking is running.</summary>
    Tracking,

    /// <summary>Tracking is paused or not available.</summary>
    Idle,
}

/// <summary>
/// System tray icon and menu (Windows Forms <see cref="Forms.NotifyIcon"/>).
/// Menu entries for features of later phases are present but disabled and labelled with their phase,
/// so nothing unimplemented appears to work.
/// </summary>
internal sealed class TrayIconService(WindowManager windows, IAppLifetime lifetime, ILogger<TrayIconService> logger) : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trackingIcon;
    private Drawing.Icon? _idleIcon;

    public void Initialize()
    {
        _trackingIcon = LoadIcon("tray-active.ico");
        _idleIcon = LoadIcon("tray-paused.ico");

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Pending("Record activity now", 3));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Open WorkTrace", windows.ShowMainWindow, bold: true));
        menu.Items.Add(Pending("Activity log", 4));
        menu.Items.Add(Pending("Weekly overview", 6));
        menu.Items.Add(Pending("Screenshot history", 5));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Pending("Pause tracking", 3));
        menu.Items.Add(Pending("Enable screenshots", 5));
        menu.Items.Add(Pending("Settings", 7));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Exit", lifetime.RequestExit));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => windows.ShowMainWindow();
        SetState(TrayState.Idle, "WorkTrace – check-in tracking arrives in Phase 3");

        logger.LogInformation("Tray icon initialized");
    }

    public void SetState(TrayState state, string tooltip)
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Icon = state == TrayState.Tracking ? _trackingIcon : _idleIcon;

        // NotifyIcon.Text is limited to 127 characters.
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    public void ShowNotification(string title, string text)
    {
        _notifyIcon?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        if (_notifyIcon is not null)
        {
            // Hide first so the icon does not linger in the tray until the mouse passes over it.
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _trackingIcon?.Dispose();
        _idleIcon?.Dispose();
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        if (bold)
        {
            item.Font = new Drawing.Font(item.Font, Drawing.FontStyle.Bold);
        }

        return item;
    }

    private static Forms.ToolStripMenuItem Pending(string text, int phase) =>
        new($"{text}  (Phase {phase})") { Enabled = false };

    private static Drawing.Icon LoadIcon(string fileName)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/Assets/{fileName}"))
            ?? throw new InvalidOperationException($"Icon resource '{fileName}' not found.");
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
