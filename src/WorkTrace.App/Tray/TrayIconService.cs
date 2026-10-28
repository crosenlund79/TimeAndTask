using System.ComponentModel;
using System.Windows;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Tracking;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace WorkTrace.App.Tray;

/// <summary>
/// System tray icon and menu. Recording and tracking controls come first; the icon and tooltip
/// reflect whether time is being tracked.
/// </summary>
internal sealed class TrayIconService(
    WindowManager windows,
    IAppLifetime lifetime,
    TrackingController tracking,
    WorkTrace.Core.Settings.ISettingsStore settings,
    ILogger<TrayIconService> logger) : IDisposable
{
    private Forms.NotifyIcon? _notifyIcon;
    private Drawing.Icon? _trackingIcon;
    private Drawing.Icon? _idleIcon;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _resumeItem;
    private Forms.ToolStripMenuItem? _recordItem;

    public void Initialize()
    {
        _trackingIcon = LoadIcon("tray-active.ico");
        _idleIcon = LoadIcon("tray-paused.ico");

        _statusItem = new Forms.ToolStripMenuItem { Enabled = false };
        var record = Item("Record activity now", () => tracking.RecordNowCommand.Execute(null), bold: true);
        _recordItem = record;
        UpdateShortcut(settings.Current.General.GlobalShortcut);

        _pauseItem = new Forms.ToolStripMenuItem("Pause tracking");
        _pauseItem.DropDownItems.Add(Item("For 15 minutes", () => tracking.PauseCommand.Execute("15")));
        _pauseItem.DropDownItems.Add(Item("For 30 minutes", () => tracking.PauseCommand.Execute("30")));
        _pauseItem.DropDownItems.Add(Item("For 1 hour", () => tracking.PauseCommand.Execute("60")));
        _pauseItem.DropDownItems.Add(Item("For 2 hours", () => tracking.PauseCommand.Execute("120")));
        _pauseItem.DropDownItems.Add(new Forms.ToolStripSeparator());
        _pauseItem.DropDownItems.Add(Item("Until I resume", () => tracking.PauseCommand.Execute("0")));
        _resumeItem = Item("Resume tracking", () => tracking.ResumeCommand.Execute(null));

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(record);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Open WorkTrace", windows.ShowMainWindow));
        menu.Items.Add(Item("Tasks and clients", () => windows.ShowMainWindow(AppSection.Tasks)));
        menu.Items.Add(Item("Settings", () => windows.ShowMainWindow(AppSection.Settings)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Exit", lifetime.RequestExit));

        _notifyIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => windows.ShowMainWindow();

        tracking.PropertyChanged += OnTrackingChanged;
        UpdateFromStatus();
        logger.LogInformation("Tray icon initialized");
    }

    /// <summary>Shows the global shortcut next to "Record activity now".</summary>
    public void UpdateShortcut(string? shortcut)
    {
        if (_recordItem is not null)
        {
            _recordItem.ShortcutKeyDisplayString = shortcut;
            _recordItem.ShowShortcutKeys = !string.IsNullOrEmpty(shortcut);
        }
    }

    public void ShowNotification(string title, string text)
    {
        _notifyIcon?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        tracking.PropertyChanged -= OnTrackingChanged;
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

    private void OnTrackingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackingController.Status))
        {
            UpdateFromStatus();
        }
    }

    private void UpdateFromStatus()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        var detail = string.IsNullOrEmpty(tracking.StatusDetail) ? string.Empty : $" · {tracking.StatusDetail}";
        _statusItem!.Text = tracking.StatusText + detail;
        _pauseItem!.Visible = !tracking.IsPaused;
        _resumeItem!.Visible = tracking.IsPaused;
        _notifyIcon.Icon = tracking.IsTracking ? _trackingIcon : _idleIcon;

        // NotifyIcon.Text is limited to 127 characters.
        var tooltip = $"WorkTrace – {tracking.StatusText}{detail}";
        _notifyIcon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
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

    private static Drawing.Icon LoadIcon(string fileName)
    {
        var resource = Application.GetResourceStream(new Uri($"pack://application:,,,/WorkTrace;component/Assets/{fileName}"))
            ?? throw new InvalidOperationException($"Icon resource '{fileName}' not found.");
        using var stream = resource.Stream;
        return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }
}
