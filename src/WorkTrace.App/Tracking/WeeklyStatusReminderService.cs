using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Views;
using WorkTrace.Core.Common;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Optional weekly reporting reminder (System › Reminders): one popup for registering the week's hours and sending next
/// week's status. Due from the configured day and time; "Remind me later" (or ignoring it) asks again an hour later, "Done for
/// this week" ends it for the week and also answers that day's time registration reminder. Waits for the reminder slot,
/// scheduled check-ins, a locked PC or nobody at it, like the other reminders. UI thread only.
/// </summary>
internal sealed class WeeklyStatusReminderService(
    ISettingsStore settings,
    ISessionStateProbe session,
    ICheckInPresenter checkIns,
    TimeReminderService timeReminders,
    TrackingController tracking,
    ReminderSlot slot,
    WindowManager windows,
    WeeklyStatusService weeklyStatus,
    TimeProvider timeProvider,
    ILogger<WeeklyStatusReminderService> logger) : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckInMargin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Later = TimeSpan.FromHours(1);
    private static readonly TimeSpan AutoClose = TimeSpan.FromMinutes(30);

    private DispatcherTimer? _timer;
    private DateTime _notBeforeUtc;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
    }

    public void Dispose() => _timer?.Stop();

    private async Task CheckAsync()
    {
        try
        {
            var now = UtcTime.Now(timeProvider);
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
            if (now < _notBeforeUtc || !WeeklyReportReminder.IsDue(local, settings.Current.Reminders)
                || slot.IsBusy || checkIns.IsOpen || timeReminders.IsOpen
                || (tracking.Status.NextCheckInUtc is { } next && next > now && next - now < CheckInMargin)
                || session.IsSessionLocked() == true || Interop.NativeMethods.IdleTime() > IdleLimit)
            {
                return;
            }

            await ShowAsync(local);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Weekly reporting reminder failed");
        }
    }

    private async Task ShowAsync(DateTime local)
    {
        if (!slot.TryEnter())
        {
            return;
        }

        try
        {
            logger.LogInformation("Weekly reporting reminder shown");
            var window = new TimeReminderWindow("Ugens rapportering", WeeklyReportReminder.Message, yesNo: false, AutoClose) { ShowActivated = false };
            window.UseWeeklyActions(
                () => windows.ShowMainWindow(AppSection.Timesheet),
                async () => WeeklyStatusService.Confirmation(await weeklyStatus.CopyAsync()));
            window.Show();
            var answer = await window.Answer;
            if (answer == ReminderAnswer.Done)
            {
                var week = WeeklyReportReminder.WeekOf(DateOnly.FromDateTime(local));
                var day = TimeRegistrationReminders.AnswerDay(TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local), settings.Current.Reminders);
                await settings.UpdateAsync(s =>
                {
                    s.Reminders.WeeklyReportDoneWeek = week;
                    s.Reminders.DoneForDay = day; // the week's hours are registered: no separate daily reminder today
                });
                logger.LogInformation("Weekly reporting reminder: done for this week");
            }
            else
            {
                _notBeforeUtc = UtcTime.Now(timeProvider) + Later;
                logger.LogInformation("Weekly reporting reminder: remind later");
            }
        }
        finally
        {
            slot.Exit();
        }
    }
}
