using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<TrackingController> _logger;

    public TrackingController(TrackingEngine engine, CheckInPresenter presenter, ILogger<TrackingController> logger)
    {
        _engine = engine;
        _presenter = presenter;
        _logger = logger;
        _engine.StatusChanged += (_, status) => OnUi(() => Status = status);
        _presenter.Recorded += (_, observation) => CheckInRecorded?.Invoke(this, observation);
        Status = engine.Status;
    }

    /// <summary>Raised on the UI thread after any check-in was recorded.</summary>
    public event EventHandler<ObservationSummary>? CheckInRecorded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTracking), nameof(IsPaused), nameof(StatusText), nameof(StatusDetail))]
    public partial TrackingStatus Status { get; private set; }

    public bool IsTracking => Status.State == TrackingState.Tracking;

    public bool IsPaused => Status.State == TrackingState.Paused;

    public string StatusText => Status.State switch
    {
        TrackingState.Tracking => "Tracking",
        TrackingState.Paused => Status.PausedUntilUtc is { } until ? $"Paused until {Local(until):HH:mm}" : "Paused",
        TrackingState.Suspended => "Computer asleep",
        _ => "Starting…",
    };

    public string StatusDetail => Status.State switch
    {
        TrackingState.Tracking when !Status.PeriodicPromptsEnabled => "Periodic check-ins are off",
        TrackingState.Tracking when Status.NextCheckInUtc is { } next => $"Next check-in at {Local(next):HH:mm}",
        TrackingState.Paused => "No time is recorded while paused",
        _ => string.Empty,
    };

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
    private Task TriggerScheduledNowAsync() => RunAsync(_engine.TriggerCheckInNowAsync);

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
