using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Reminders for minor tasks: when an open minor task's reminder time has come (also one that passed while WorkTrace
/// was not running), a WorkTrace popup offers Mark done / Remind me later; a recurring one Done for now / Snooze / Edit /
/// Disable reminder (a missed recurring occurrence is shown at most once, see <see cref="IMinorTaskService.GetDueAsync"/>). Ignoring or "later" snoozes it for
/// <see cref="Snooze"/> without changing the reminder time; completed minor tasks are never reminded. One popup at a
/// time, never together with a check-in or another reminder. UI thread only.
/// </summary>
internal sealed class MinorTaskReminderService(
    IMinorTaskService minorTasks,
    ISessionStateProbe session,
    ICheckInPresenter checkIns,
    TimeReminderService timeReminders,
    ReminderSlot slot,
    WorkTrace.App.Shell.WindowManager windows,
    TimeProvider timeProvider,
    ILogger<MinorTaskReminderService> logger) : IDisposable
{
    public static readonly TimeSpan Snooze = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AutoClose = TimeSpan.FromMinutes(10);

    private DispatcherTimer? _timer;
    private bool _busy;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();

        // Outstanding reminders from while WorkTrace was closed: shortly after start-up.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await CheckAsync();
        };
        first.Start();
    }

    public void Dispose() => _timer?.Stop();

    private async Task CheckAsync()
    {
        if (_busy || slot.IsBusy || checkIns.IsOpen || timeReminders.IsOpen || session.IsSessionLocked() == true)
        {
            return;
        }

        try
        {
            _busy = true;
            var due = await minorTasks.GetDueAsync(UtcTime.Now(timeProvider));
            if (due.Count > 0 && slot.TryEnter())
            {
                try
                {
                    await ShowAsync(due[0], due.Count - 1);
                }
                finally
                {
                    slot.Exit();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Minor task reminder failed");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ShowAsync(MinorTaskSummary task, int more)
    {
        // Snoozed before showing: an ignored, closed or crashed popup comes back after the snooze, not every minute.
        await minorTasks.SnoozeAsync(task.Id, UtcTime.Now(timeProvider) + Snooze);
        logger.LogInformation("Minor task reminder shown ({More} more due)", more);

        var message = task.Title
            + (string.IsNullOrWhiteSpace(task.Description) ? "" : "\n" + task.Description)
            + (task.ClientName is { } client ? $"\n{client}" : "")
            + (more > 0 ? $"\n\n({more} more minor task{(more == 1 ? "" : "s")} due)" : "");
        var labels = task.IsRecurring ? ("Done for now", "Snooze", " · recurring reminder") : ("Mark done", "Remind me later", " · minor task");
        var window = new TimeReminderWindow("Reminder", message, yesNo: true, AutoClose, labels, showEdit: true, showDisable: task.IsRecurring)
        {
            ShowActivated = false,
        };
        window.Show();
        switch (await window.Answer)
        {
            case ReminderAnswer.Done:
                // One-time: completed. Recurring: only this occurrence; the next one is scheduled (no new record).
                await minorTasks.AcknowledgeAsync(task.Id);
                break;
            case ReminderAnswer.Disable:
                await minorTasks.SetReminderDisabledAsync(task.Id, true);
                break;
            case ReminderAnswer.Edit:
                // The snooze above keeps it quiet while it is edited; a changed reminder time starts afresh, Done ends it.
                windows.ShowMainWindow(WorkTrace.App.Shell.AppSection.Tasks, new WorkTrace.App.Shell.EditMinorTaskRequest(task.Id));
                break;
        }

        MinorTasksViewModel.RaiseChanged();
    }
}
