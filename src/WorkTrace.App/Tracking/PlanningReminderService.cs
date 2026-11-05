using System.Text;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.App.Views;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Optional daily "review your upcoming tasks" popup listing the start of the active pipeline, with Open timeline /
/// Dismiss. Waits while another popup is open, near a scheduled check-in, or while nobody is at the PC. UI thread only.
/// </summary>
internal sealed class PlanningReminderService(
    ISettingsStore settings,
    ITaskService tasks,
    ISessionStateProbe session,
    ICheckInPresenter checkIns,
    TimeReminderService timeReminders,
    TrackingController tracking,
    ReminderSlot slot,
    WindowManager windows,
    TimeProvider timeProvider,
    ILogger<PlanningReminderService> logger) : IDisposable
{
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CheckInMargin = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AutoClose = TimeSpan.FromMinutes(10);

    private DispatcherTimer? _timer;

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
            var s = settings.Current.Tasks;
            var now = UtcTime.Now(timeProvider);
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local);
            var today = DateOnly.FromDateTime(local);
            if (!s.PlanningReminderEnabled || s.PlanningReminderShownOn == today || TimeOnly.FromDateTime(local) < s.PlanningReminderTime
                || (s.PlanningReminderWeekdaysOnly && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                || slot.IsBusy || checkIns.IsOpen || timeReminders.IsOpen
                || (tracking.Status.NextCheckInUtc is { } next && next > now && next - now < CheckInMargin)
                || session.IsSessionLocked() == true || Interop.NativeMethods.IdleTime() > IdleLimit)
            {
                return;
            }

            await ShowAsync(today);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Planning reminder failed");
        }
    }

    private async Task ShowAsync(DateOnly today)
    {
        if (!slot.TryEnter())
        {
            return;
        }

        try
        {
            await settings.UpdateAsync(x => x.Tasks.PlanningReminderShownOn = today);
            var queue = TaskPipeline.Build(await tasks.SearchAsync(new TaskQuery(Limit: 500)));
            var text = new StringBuilder();
            foreach (var entry in queue.Take(5))
            {
                text.Append("• ").Append(entry.Task.Title).Append(" — ")
                    .AppendLine(entry.IsProvisional ? "no estimate" : TaskPipeline.FormatDuration(entry.DurationMinutes));
            }

            if (queue.Count > 5)
            {
                text.Append("…and ").Append(queue.Count - 5).AppendLine(" more.");
            }

            if (queue.Count == 0)
            {
                text.AppendLine("No active tasks in the pipeline.");
            }

            logger.LogInformation("Planning reminder shown ({Count} active tasks)", queue.Count);
            var window = new TimeReminderWindow("Review your upcoming tasks", text.ToString().TrimEnd(), yesNo: true, AutoClose,
                ("Open timeline", "Dismiss", " · planning")) { ShowActivated = false };
            window.Show();
            if (await window.Answer == ReminderAnswer.Done)
            {
                windows.ShowMainWindow(AppSection.Tasks, new ShowTimelineRequest());
            }
        }
        finally
        {
            slot.Exit();
        }
    }
}
