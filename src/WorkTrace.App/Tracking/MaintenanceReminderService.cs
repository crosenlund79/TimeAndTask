using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Views;
using WorkTrace.Core.Common;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// "Have you kept your Tasks and Board up to date?" after a while without using the WorkTrace window. Never shown
/// together with a check-in or a time registration reminder, close to a scheduled check-in, or while nobody is at the
/// PC. State is kept in memory (a restart counts as a fresh start). Use on the UI thread.
/// </summary>
public sealed class MaintenanceReminderService(
    ISettingsStore settings,
    ISessionStateProbe session,
    ICheckInPresenter checkIns,
    TimeReminderService timeReminders,
    TrackingController tracking,
    TimeProvider timeProvider,
    ILogger<MaintenanceReminderService> logger) : IDisposable
{
    public const string Message = "Just a friendly nudge! Have you kept your Tasks and Board up to date? 📋";

    private static readonly TimeSpan Snooze = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckInMargin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AutoClose = TimeSpan.FromMinutes(5);

    private DispatcherTimer? _timer;
    private DateTime _lastUsedUtc;
    private DateTime _notBeforeUtc;
    private bool _open;

    public void Start()
    {
        _lastUsedUtc = Now;
        InputManager.Current.PostProcessInput += OnInput;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    public void Dispose()
    {
        _timer?.Stop();
        InputManager.Current.PostProcessInput -= OnInput;
    }

    private DateTime Now => UtcTime.Now(timeProvider);

    /// <summary>Keys and clicks in the main window count as using WorkTrace (answering popups does not).</summary>
    private void OnInput(object sender, ProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is KeyEventArgs or MouseButtonEventArgs
            && e.StagingItem.Input.Source is DependencyObject source
            && Window.GetWindow(source) is { } window && window == Application.Current?.MainWindow)
        {
            _lastUsedUtc = Now;
        }
    }

    private async Task CheckAsync()
    {
        try
        {
            var s = settings.Current.Tasks;
            var now = Now;
            if (!s.MaintenanceReminderEnabled || _open || now < _notBeforeUtc || now - _lastUsedUtc < TimeSpan.FromMinutes(s.MaintenanceReminderMinutes)
                || checkIns.IsOpen || timeReminders.IsOpen
                || tracking.Status.NextCheckInUtc is { } next && next - now < CheckInMargin && next > now
                || session.IsSessionLocked() == true || Interop.NativeMethods.IdleTime() > IdleLimit)
            {
                return;
            }

            await ShowAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Maintenance reminder failed");
        }
    }

    private async Task ShowAsync()
    {
        _open = true;
        try
        {
            logger.LogInformation("Tasks & Board maintenance reminder shown");
            var window = new TimeReminderWindow("Tasks & Board", Message, yesNo: true, AutoClose, ("All up to date", "Remind me later", " · tasks & board"))
            {
                ShowActivated = false,
            };
            window.Show();
            var answer = await window.Answer;
            if (answer == ReminderAnswer.Done)
            {
                _lastUsedUtc = Now;
            }
            else
            {
                _notBeforeUtc = Now + Snooze;
            }
        }
        finally
        {
            _open = false;
        }
    }
}
