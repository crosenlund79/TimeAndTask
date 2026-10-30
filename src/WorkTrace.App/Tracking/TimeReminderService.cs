using System.Media;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Views;
using WorkTrace.Core.Common;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// "Have you registered your time today?" reminders during the configured period, plus the best-effort
/// shutdown reminder. Answers are stored in settings.json so they survive restarts. Use on the UI thread.
/// </summary>
public sealed class TimeReminderService(ISettingsStore settings, ISessionStateProbe session, TimeProvider timeProvider, ILogger<TimeReminderService> logger)
    : IDisposable
{
    /// <summary>Nobody at the keyboard for this long: wait (avoids popups piling up at an empty desk overnight).</summary>
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutoClose = TimeSpan.FromMinutes(5);

    private DispatcherTimer? _timer;
    private TimeReminderWindow? _open;
    private int _counter = Random.Shared.Next(100);

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();

    private DateTime Now => UtcTime.Now(timeProvider);

    private DateTime Local => TimeZoneInfo.ConvertTimeFromUtc(Now, TimeZoneInfo.Local);

    private async Task CheckAsync()
    {
        try
        {
            if (_open is not null || !TimeRegistrationReminders.IsDue(Now, TimeZoneInfo.Local, settings.Current.Reminders)
                || session.IsSessionLocked() == true || Interop.NativeMethods.IdleTime() > IdleLimit)
            {
                return;
            }

            await ShowAsync(test: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Time registration reminder failed");
        }
    }

    /// <summary>Shows the periodic reminder. A test shows it without changing any reminder state.</summary>
    public async Task ShowAsync(bool test)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }

        var s = settings.Current.Reminders;
        var day = TimeRegistrationReminders.AnswerDay(Local, s);
        if (!test)
        {
            // Counts as shown even if ignored: the next one comes one interval later (also after a restart).
            await SaveAsync(r => r.NotBeforeUtc = Now.AddMinutes(r.IntervalMinutes));
        }

        logger.LogInformation("Time registration reminder shown{Test}", test ? " (test)" : "");
        var answer = await ShowWindowAsync("Have you registered your time today?", TimeRegistrationReminders.Message(_counter++), yesNo: false, s.PlaySound);
        if (test)
        {
            return;
        }

        switch (answer)
        {
            case ReminderAnswer.Done:
            case ReminderAnswer.Tomorrow:
                await SaveAsync(r => r.DoneForDay = day);
                break;
            default:
                await SaveAsync(r => r.NotBeforeUtc = Now.AddMinutes(r.IntervalMinutes));
                break;
        }
    }

    /// <summary>
    /// Windows is ending the session. A dialog cannot be shown reliably now (and must not delay shutdown), so the
    /// outstanding day is remembered and the question is asked at the next start. Synchronous on purpose.
    /// </summary>
    public void OnSessionEnding()
    {
        var s = settings.Current.Reminders;
        if (!s.OnShutdown || !TimeRegistrationReminders.IsOutstanding(Now, TimeZoneInfo.Local, s))
        {
            return;
        }

        var day = TimeRegistrationReminders.AnswerDay(Local, s);
        try
        {
            Task.Run(() => settings.UpdateAsync(x => x.Reminders.AskAfterShutdownForDay = day)).Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remember the shutdown reminder");
        }
    }

    /// <summary>At start-up: asks about the day Windows shut down on while registration was outstanding.</summary>
    public async Task AskAfterShutdownAsync()
    {
        if (settings.Current.Reminders.AskAfterShutdownForDay is not { } day)
        {
            return;
        }

        await SaveAsync(r => r.AskAfterShutdownForDay = null);
        if (settings.Current.Reminders.DoneForDay == day)
        {
            return;
        }

        var when = day == DateOnly.FromDateTime(Local) ? "earlier today" : $"on {day:dddd d MMMM}";
        var answer = await ShowWindowAsync(
            "Before you disappeared...",
            $"...{when}: had you registered your time? 😇",
            yesNo: true,
            settings.Current.Reminders.PlaySound);
        if (answer == ReminderAnswer.Done)
        {
            await SaveAsync(r => r.DoneForDay = day);
        }
    }

    /// <summary>Exit from the tray: asks once before WorkTrace closes, when enabled and still outstanding.</summary>
    public async Task AskBeforeExitAsync()
    {
        var s = settings.Current.Reminders;
        if (!s.OnShutdown || !TimeRegistrationReminders.IsOutstanding(Now, TimeZoneInfo.Local, s))
        {
            return;
        }

        var day = TimeRegistrationReminders.AnswerDay(Local, s);
        var answer = await ShowWindowAsync(TimeRegistrationReminders.Message(_counter++, shutdown: true), "WorkTrace is closing. Mark today as done?",
            yesNo: true, s.PlaySound, activate: true);
        if (answer == ReminderAnswer.Done)
        {
            await SaveAsync(r => r.DoneForDay = day);
        }
    }

    private async Task<ReminderAnswer> ShowWindowAsync(string heading, string message, bool yesNo, bool sound, bool activate = false)
    {
        var window = new TimeReminderWindow(heading, message, yesNo, AutoClose) { ShowActivated = activate };
        _open = window;
        try
        {
            if (sound)
            {
                SystemSounds.Asterisk.Play();
            }

            window.Show();
            return await window.Answer;
        }
        finally
        {
            _open = null;
        }
    }

    private async Task SaveAsync(Action<ReminderSettings> change)
    {
        try
        {
            await settings.UpdateAsync(s => change(s.Reminders));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reminder state could not be saved");
        }
    }
}
