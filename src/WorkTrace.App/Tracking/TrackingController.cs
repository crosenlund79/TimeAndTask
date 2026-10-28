using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Shell;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// UI-facing wrapper around <see cref="TrackingEngine"/>: status marshalled to the UI thread plus the
/// commands shared by the tray menu, the overview page and the settings page.
/// </summary>
public sealed partial class TrackingController : ObservableObject
{
    private readonly TrackingEngine _engine;
    private readonly CheckInPresenter _presenter;
    private readonly INotificationService _notifications;
    private readonly ILogger<TrackingController> _logger;

    public TrackingController(TrackingEngine engine, CheckInPresenter presenter, INotificationService notifications, ILogger<TrackingController> logger)
    {
        _engine = engine;
        _presenter = presenter;
        _notifications = notifications;
        _logger = logger;
        _engine.StatusChanged += (_, status) => OnUi(() => Status = status);
        _presenter.Recorded += (_, observation) => CheckInRecorded?.Invoke(this, observation);
        Status = engine.Status;
    }

    /// <summary>Raised on the UI thread after any check-in was recorded.</summary>
    public event EventHandler<ObservationSummary>? CheckInRecorded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTracking), nameof(IsPaused), nameof(StatusText), nameof(StatusDetail), nameof(LastAttemptText), nameof(Warning), nameof(HasWarning))]
    public partial TrackingStatus Status { get; private set; }

    public bool IsTracking => Status.State == TrackingState.Tracking;

    public bool IsPaused => Status.State == TrackingState.Paused;

    public string StatusText => Status.State switch
    {
        TrackingState.Tracking when !Status.PeriodicPromptsEnabled => "Active · check-ins off",
        TrackingState.Tracking => "Active",
        TrackingState.Paused => Status.PausedUntilUtc is { } until ? $"Paused until {Local(until):HH:mm}" : "Paused",
        TrackingState.Suspended => "Computer asleep",
        TrackingState.Error => "Check-ins need attention",
        _ => "Starting…",
    };

    public string StatusDetail => Status.State switch
    {
        TrackingState.Tracking when !Status.PeriodicPromptsEnabled => "Periodic check-ins are turned off in Settings",
        TrackingState.Tracking when Status.NextCheckInUtc is { } next => $"Next check-in at {Local(next):HH:mm} · every {Interval(Status.IntervalMinutes)}",
        TrackingState.Paused => "No time is recorded while paused",
        TrackingState.Error => Status.LastError ?? "Scheduling failed; see the log",
        _ => string.Empty,
    };

    /// <summary>When the last check-in was attempted and what happened (empty before the first one).</summary>
    public string? LastAttemptText => Status.LastAttempt is { } attempt
        ? $"Last check-in {Local(attempt.AtUtc):HH:mm}: {Describe(attempt.Result)}"
        : null;

    /// <summary>Why check-ins may not appear, so it never fails silently; null when all is well.</summary>
    public string? Warning => Status switch
    {
        { State: TrackingState.Error } => "Scheduled check-ins failed. WorkTrace retries every minute; details are in the log.",
        { State: TrackingState.Tracking, IsSessionLocked: true } => "Windows reports the session as locked, so scheduled check-ins are held back.",
        { LastAttempt.Result: CheckInAttemptResult.Failed } => "The last check-in popup could not be shown; details are in the log.",
        _ => null,
    };

    public bool HasWarning => Warning is not null;

    [RelayCommand]
    private void RecordNow() => _presenter.ShowManual();

    /// <param name="minutes">Pause length in minutes; "0" or empty pauses until resumed.</param>
    [RelayCommand]
    private Task PauseAsync(string? minutes) =>
        RunAsync(() => _engine.PauseAsync(int.TryParse(minutes, out var m) && m > 0 ? TimeSpan.FromMinutes(m) : null));

    [RelayCommand]
    private Task ResumeAsync() => RunAsync(_engine.ResumeAsync);

    /// <summary>Shows a real scheduled check-in now (popup mode, countdown and missed handling as configured).</summary>
    [RelayCommand]
    private async Task TriggerScheduledNowAsync()
    {
        try
        {
            var message = await _engine.TriggerCheckInNowAsync() switch
            {
                CheckInAttemptResult.Shown => null,
                CheckInAttemptResult.SkippedPopupOpen => "A check-in is already open. Answer or close it first.",
                CheckInAttemptResult.SuppressedLocked => "Windows reports the session as locked, so the check-in was held back.",
                _ => "Tracking is not active (paused or asleep). Resume tracking to show a check-in.",
            };
            if (message is not null)
            {
                _notifications.Show(message, NotificationKind.Warning);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Showing a check-in now failed");
            _notifications.Show("The check-in could not be shown. Details are in the log.", NotificationKind.Error);
        }
    }

    internal static string Describe(CheckInAttemptResult result) => result switch
    {
        CheckInAttemptResult.Shown => "shown",
        CheckInAttemptResult.Answered => "answered",
        CheckInAttemptResult.Missed => "missed (closed after the countdown)",
        CheckInAttemptResult.Dismissed => "dismissed",
        CheckInAttemptResult.SkippedPopupOpen => "skipped, a check-in was already open",
        CheckInAttemptResult.SuppressedLocked => "held back, session locked",
        CheckInAttemptResult.NotTracking => "not shown, tracking not active",
        _ => "failed",
    };

    private static string Interval(int minutes) => minutes switch
    {
        1 => "minute",
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 => minutes == 60 ? "hour" : $"{minutes / 60} hours",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Changing the tracking state failed");
        }
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local);

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }
}
