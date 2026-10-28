using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace WorkTrace.App.Shell;

public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A transient message shown at the bottom of the main window, optionally with one action (e.g. Undo).</summary>
public sealed record Notification(string Message, NotificationKind Kind, string? ActionText, IAsyncRelayCommand? Action);

public interface INotificationService
{
    Notification? Current { get; }

    void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Func<Task>? action = null);

    void Dismiss();
}

/// <summary>
/// Holds the single current notification. Messages auto-dismiss (longer when they offer an action);
/// a newer message replaces an older one. Call from the UI thread.
/// </summary>
public sealed partial class NotificationService : ObservableObject, INotificationService
{
    private static readonly TimeSpan PlainDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ActionDuration = TimeSpan.FromSeconds(10);

    private int _version;

    [ObservableProperty]
    public partial Notification? Current { get; private set; }

    public void Show(string message, NotificationKind kind = NotificationKind.Info, string? actionText = null, Func<Task>? action = null)
    {
        var version = ++_version;
        IAsyncRelayCommand? command = action is null
            ? null
            : new AsyncRelayCommand(async () =>
            {
                Dismiss();
                await action();
            });

        Current = new Notification(message, kind, actionText, command);
        _ = AutoDismissAsync(version, action is null && kind != NotificationKind.Error ? PlainDuration : ActionDuration);
    }

    [RelayCommand]
    public void Dismiss()
    {
        _version++;
        Current = null;
    }

    private async Task AutoDismissAsync(int version, TimeSpan delay)
    {
        await Task.Delay(delay);
        if (version == _version)
        {
            Current = null;
        }
    }
}
