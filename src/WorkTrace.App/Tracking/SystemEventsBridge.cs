using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>Forwards Windows power, session and clock notifications to the tracking engine.</summary>
internal sealed class SystemEventsBridge(TrackingEngine engine, ILogger<SystemEventsBridge> logger) : IDisposable
{
    private bool _subscribed;

    public void Start()
    {
        if (_subscribed)
        {
            return;
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.TimeChanged += OnTimeChanged;
        _subscribed = true;
    }

    public void Dispose()
    {
        if (!_subscribed)
        {
            return;
        }

        // SystemEvents are static: always unsubscribe to avoid leaks.
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.TimeChanged -= OnTimeChanged;
        _subscribed = false;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // Synchronous on purpose: Windows may suspend as soon as this handler returns.
                Run(engine.NotifySuspendAsync, wait: true);
                break;
            case PowerModes.Resume:
                Run(engine.NotifyResumeAsync);
                break;
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                Run(engine.NotifySessionLockedAsync);
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                Run(engine.NotifySessionUnlockedAsync);
                break;
        }
    }

    private void OnTimeChanged(object? sender, EventArgs e) => Run(engine.NotifyTimeChangedAsync);

    private void Run(Func<Task> action, bool wait = false)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handling a system notification failed");
            }
        });

        if (wait)
        {
            task.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
