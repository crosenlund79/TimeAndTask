using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.Infrastructure.Tracking;

/// <summary>
/// Owns tracking state: tracking segments, the check-in schedule, pause/resume and reactions to
/// sleep, session lock and clock changes.
/// <para>
/// One timer is armed for the next thing that must happen (prompt, heartbeat, end of a timed pause);
/// there is no polling. All state changes are serialised through a single gate. Popups are shown
/// without holding the gate, and their outcome is written back when they close.
/// </para>
/// <para>
/// Invariants: never fabricate tracking (segments end at the moment tracking stops), never show a
/// backlog of prompts (the next slot is always computed from "now"), and an unanswered prompt is
/// stored as missed/dismissed/suppressed so the time after it becomes uncertain.
/// </para>
/// </summary>
public sealed class TrackingEngine : IAsyncDisposable
{
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan ClockJumpTolerance = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan StartPromptDelay = TimeSpan.FromSeconds(20);
    internal static readonly TimeSpan WelcomeBackDelay = TimeSpan.FromSeconds(5);

    private readonly ITrackingStore _store;
    private readonly ISettingsStore _settings;
    private readonly ICheckInPresenter _presenter;
    private readonly TimeProvider _time;
    private readonly TimeProvider _monotonic;
    private readonly ILogger<TrackingEngine> _logger;
    private readonly TimeZoneInfo _timeZone;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _statusLock = new();

    private ITimer? _timer;
    private Task _lastTimerRun = Task.CompletedTask;
    private Task _lastPromptRun = Task.CompletedTask;
    private bool _started;
    private bool _stopped;
    private bool _paused;
    private bool _suspended;
    private bool _locked;
    private bool _suppressedWhileLocked;

    private int? _segmentId;
    private int? _lockId;
    private int? _openPromptId;
    private DateTime? _nextPromptUtc;
    private (DateTime DueUtc, PromptReason Reason)? _extraPrompt;
    private DateTime? _lastPromptUtc;
    private DateTime _baselineWallUtc;
    private long _baselineTimestamp;
    private CheckInAttempt? _lastAttempt;
    private string? _lastError;
    private readonly ISessionStateProbe? _sessionProbe;

    /// <param name="monotonicClock">
    /// Source of elapsed time unaffected by wall-clock changes (defaults to <paramref name="timeProvider"/>'s
    /// timestamp). Separate only so tests can simulate the system clock jumping.
    /// </param>
    public TrackingEngine(
        ITrackingStore store,
        ISettingsStore settings,
        ICheckInPresenter presenter,
        TimeProvider timeProvider,
        ILogger<TrackingEngine> logger,
        TimeZoneInfo? timeZone = null,
        TimeProvider? monotonicClock = null,
        ISessionStateProbe? sessionProbe = null)
    {
        _sessionProbe = sessionProbe;
        _store = store;
        _settings = settings;
        _presenter = presenter;
        _time = timeProvider;
        _monotonic = monotonicClock ?? timeProvider;
        _logger = logger;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Raised after every state change (on the thread that caused it).</summary>
    public event EventHandler<TrackingStatus>? StatusChanged;

    public TrackingStatus Status { get; private set; } = TrackingStatus.Initial;

    private TrackingSettings Tracking => _settings.Current.Tracking;

    private DateTime Now => UtcTime.Now(_time);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_started)
            {
                return;
            }

            var repaired = await _store.RecoverAsync(cancellationToken);
            _started = true;
            _timer = _time.CreateTimer(_ => _lastTimerRun = OnTimerAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _settings.Changed += OnSettingsChanged;

            var now = Now;
            _paused = IsPausedBySettings(now);
            if (!_paused)
            {
                await BeginSegmentAsync(now, PromptReason.TrackingStarted, cancellationToken);
            }

            _logger.LogInformation("Tracking engine started ({State}; {Repaired} segment(s) recovered)", _paused ? "paused" : "tracking", repaired);
            AfterChange();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Ends tracking cleanly (application exit). Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started || _stopped)
            {
                return;
            }

            _stopped = true;
            _settings.Changed -= OnSettingsChanged;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await _shutdown.CancelAsync();

            var now = Now;
            if (_openPromptId is { } promptId)
            {
                await _store.ResolvePromptAsync(promptId, CheckInOutcome.Missed, now);
            }

            await EndSegmentAsync(now, TrackingEndReason.Shutdown, CancellationToken.None);
            _logger.LogInformation("Tracking engine stopped");
            AfterChange();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Pauses tracking for <paramref name="duration"/>, or until resumed when null.</summary>
    public Task PauseAsync(TimeSpan? duration) => WithGateAsync(async ct =>
    {
        var now = Now;
        await SavePauseSettingsAsync(enabled: duration is not null, pausedUntil: duration is { } d ? now + d : null, ct);
        if (!_paused)
        {
            _paused = true;
            await EndSegmentAsync(now, TrackingEndReason.Paused, ct);
            _logger.LogInformation("Tracking paused {Duration}", duration is null ? "until resumed" : $"for {duration}");
        }
    });

    public Task ResumeAsync() => WithGateAsync(async ct =>
    {
        await SavePauseSettingsAsync(enabled: true, pausedUntil: null, ct);
        await ResumeCoreAsync(Now, ct);
    });

    /// <summary>The computer is going to sleep: stop counting time immediately.</summary>
    public Task NotifySuspendAsync() => WithGateAsync(async ct =>
    {
        _suspended = true;
        await EndSegmentAsync(Now, TrackingEndReason.Sleep, ct);
    });

    /// <summary>The computer woke up: start a new segment; never replay prompts that were due while asleep.</summary>
    public Task NotifyResumeAsync() => WithGateAsync(async ct =>
    {
        _suspended = false;
        var now = Now;
        if (!_paused && _segmentId is null)
        {
            await BeginSegmentAsync(now, PromptReason.Resumed, ct);
        }
    });

    public Task NotifySessionLockedAsync() => WithGateAsync(async ct =>
    {
        _locked = true;
        if (_segmentId is not null && _lockId is null)
        {
            _lockId = await _store.StartLockAsync(Now, ct);
        }
    });

    public Task NotifySessionUnlockedAsync() => WithGateAsync(async ct =>
    {
        _locked = false;
        await EndLockAsync(Now, ct);

        // Check-ins came due while away: ask once now that the user is back.
        if (_suppressedWhileLocked && _segmentId is not null && Tracking.PeriodicPromptsEnabled)
        {
            _extraPrompt = (Now + WelcomeBackDelay, PromptReason.WelcomeBack);
        }

        _suppressedWhileLocked = false;
    });

    /// <summary>
    /// Shows a real scheduled check-in immediately (Settings "show now"), honouring the popup mode and
    /// auto-dismiss, so behaviour can be tried without waiting for the next slot. Returns what happened,
    /// e.g. <see cref="CheckInAttemptResult.NotTracking"/> while paused, so the UI can explain it.
    /// </summary>
    public async Task<CheckInAttemptResult> TriggerCheckInNowAsync()
    {
        var result = CheckInAttemptResult.NotTracking;
        await WithGateAsync(async ct =>
        {
            var now = Now;
            if (_segmentId is null)
            {
                result = Record(now, CheckInAttemptResult.NotTracking, "requested now");
                return;
            }

            result = await TriggerPromptAsync(now, PromptReason.Scheduled, now, ct);
            _nextPromptUtc = ComputeNextPrompt(now);
        });

        return result;
    }

    /// <summary>The system clock was changed; split the segment if the wall clock jumped.</summary>
    public Task NotifyTimeChangedAsync() => WithGateAsync(ct => DetectClockJumpAsync(Now, ct));

    /// <summary>Waits until timer work, and the bookkeeping of popups that already closed, has finished (tests).</summary>
    internal async Task IdleAsync()
    {
        await _lastTimerRun;

        // An open popup completes only when the user (or test) closes it.
        if (!_presenter.IsOpen)
        {
            await _lastPromptRun;
        }

        await _gate.WaitAsync();
        _gate.Release();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_timer is not null)
        {
            await _timer.DisposeAsync();
        }

        _shutdown.Dispose();
        _gate.Dispose();
    }

    // ---------------------------------------------------------------- timer

    private async Task OnTimerAsync()
    {
        try
        {
            await WithGateAsync(async ct =>
            {
                var now = Now;
                _lastError = null;
                await DetectClockJumpAsync(now, ct);

                if (_paused && Tracking.PausedUntilUtc is { } until && until <= now && Tracking.Enabled)
                {
                    await SavePauseSettingsAsync(enabled: true, pausedUntil: null, ct);
                    await ResumeCoreAsync(now, ct);
                    return;
                }

                if (_segmentId is not { } segmentId)
                {
                    return;
                }

                if (now - _baselineWallUtc >= HeartbeatInterval)
                {
                    await _store.HeartbeatAsync(segmentId, now, ct);
                    SetBaseline(now);
                }

                if (_extraPrompt is { } extra && extra.DueUtc <= now)
                {
                    _extraPrompt = null;
                    await TriggerPromptAsync(now, extra.Reason, now, ct);
                }
                else if (_nextPromptUtc is { } due && due <= now)
                {
                    // Scheduled at the slot time: that is where uncertainty starts if unanswered.
                    await TriggerPromptAsync(due, PromptReason.Scheduled, now, ct);
                }

                _nextPromptUtc = ComputeNextPrompt(now);
            });
        }
        catch (Exception ex) when (ex is not ObjectDisposedException)
        {
            _logger.LogError(ex, "Tracking timer failed; retrying in one minute");
            _lastError = $"Scheduling failed: {ex.Message}";
            PublishStatus();
            _timer?.Change(TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task<CheckInAttemptResult> TriggerPromptAsync(DateTime scheduledUtc, PromptReason reason, DateTime now, CancellationToken ct)
    {
        _lastPromptUtc = now;

        if (_presenter.IsOpen)
        {
            // The user is already looking at a check-in; a second popup would only get in the way.
            return Record(now, CheckInAttemptResult.SkippedPopupOpen, reason.ToString());
        }

        if (_locked && _sessionProbe?.IsSessionLocked() == false)
        {
            // A lock notification without a matching unlock (e.g. remote-desktop reconnects) must not
            // silence check-ins forever: trust the actual session state.
            _logger.LogWarning("Session reported as locked but is not; clearing the lock state");
            _locked = false;
            _suppressedWhileLocked = false;
            await EndLockAsync(now, ct);
        }

        if (_locked)
        {
            await _store.CreatePromptAsync(scheduledUtc, null, CheckInOutcome.Suppressed, ct);
            _suppressedWhileLocked = true;
            return Record(now, CheckInAttemptResult.SuppressedLocked, reason.ToString());
        }

        var promptId = await _store.CreatePromptAsync(scheduledUtc, now, CheckInOutcome.Pending, ct);
        _openPromptId = promptId;
        Record(now, CheckInAttemptResult.Shown, reason.ToString());
        _lastPromptRun = RunPromptAsync(promptId, scheduledUtc, reason);
        return CheckInAttemptResult.Shown;
    }

    private CheckInAttemptResult Record(DateTime now, CheckInAttemptResult result, string context)
    {
        _lastAttempt = new CheckInAttempt(now, result);
        _logger.LogInformation("Check-in attempt ({Context}): {Result}", context, result);
        return result;
    }

    private async Task RunPromptAsync(int promptId, DateTime scheduledUtc, PromptReason reason)
    {
        PromptOutcome outcome;
        try
        {
            outcome = await _presenter.ShowPromptAsync(promptId, scheduledUtc, reason, _shutdown.Token);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Check-in popup failed");
            }

            outcome = PromptOutcome.Closed;
        }

        if (_stopped)
        {
            // StopAsync already stored the outcome; the database may be gone during shutdown.
            return;
        }

        try
        {
            var stored = outcome switch
            {
                PromptOutcome.Answered => (CheckInOutcome?)null,
                PromptOutcome.Dismissed => CheckInOutcome.Dismissed,
                _ => CheckInOutcome.Missed,
            };
            if (stored is { } value)
            {
                await _store.ResolvePromptAsync(promptId, value, Now);
            }

            _lastAttempt = new CheckInAttempt(Now, outcome switch
            {
                PromptOutcome.Answered => CheckInAttemptResult.Answered,
                PromptOutcome.Dismissed => CheckInAttemptResult.Dismissed,
                PromptOutcome.TimedOut => CheckInAttemptResult.Missed,
                _ => CheckInAttemptResult.Failed,
            });
            _logger.LogInformation("Check-in popup closed: {Outcome}", outcome);
            PublishStatus();

            if (_openPromptId == promptId)
            {
                _openPromptId = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not store the check-in outcome");
        }
    }

    // ---------------------------------------------------------------- segments

    private async Task BeginSegmentAsync(DateTime now, PromptReason reason, CancellationToken ct)
    {
        _segmentId = await _store.OpenSegmentAsync(now, ct);
        SetBaseline(now);
        _nextPromptUtc = ComputeNextPrompt(now);
        _extraPrompt = Tracking.PromptOnStart && Tracking.PeriodicPromptsEnabled ? (now + StartPromptDelay, reason) : null;

        if (_locked && _lockId is null)
        {
            _lockId = await _store.StartLockAsync(now, ct);
        }
    }

    private async Task EndSegmentAsync(DateTime now, TrackingEndReason reason, CancellationToken ct)
    {
        await EndLockAsync(now, ct);
        if (_segmentId is { } id)
        {
            await _store.CloseSegmentAsync(id, now, reason, ct);
            _segmentId = null;
        }

        _nextPromptUtc = null;
        _extraPrompt = null;
    }

    private async Task EndLockAsync(DateTime now, CancellationToken ct)
    {
        if (_lockId is { } lockId)
        {
            await _store.EndLockAsync(lockId, now, ct);
            _lockId = null;
        }
    }

    private async Task ResumeCoreAsync(DateTime now, CancellationToken ct)
    {
        if (!_paused)
        {
            return;
        }

        _paused = false;
        if (!_suspended)
        {
            await BeginSegmentAsync(now, PromptReason.Resumed, ct);
        }

        _logger.LogInformation("Tracking resumed");
    }

    /// <summary>
    /// Compares wall-clock progress with monotonic elapsed time. A difference means the system clock
    /// was changed (or the machine slept without notifying us): the segment is ended at the last moment
    /// known on the old clock and a new one starts now, so estimation never spans the jump.
    /// </summary>
    private async Task DetectClockJumpAsync(DateTime now, CancellationToken ct)
    {
        if (_segmentId is not { } id)
        {
            SetBaseline(now);
            return;
        }

        var expected = _baselineWallUtc + _monotonic.GetElapsedTime(_baselineTimestamp);
        if ((now - expected).Duration() <= ClockJumpTolerance)
        {
            return;
        }

        _logger.LogWarning("System clock jump of {Jump} detected; splitting tracking segment", now - expected);
        await _store.CloseSegmentAsync(id, expected, TrackingEndReason.ClockChange, ct);
        if (_lockId is { } lockId)
        {
            await _store.EndLockAsync(lockId, expected, ct);
            _lockId = null;
        }

        _segmentId = await _store.OpenSegmentAsync(now, ct);
        if (_locked)
        {
            _lockId = await _store.StartLockAsync(now, ct);
        }

        SetBaseline(now);
        _lastPromptUtc = null;
        _nextPromptUtc = ComputeNextPrompt(now);
    }

    private void SetBaseline(DateTime now)
    {
        _baselineWallUtc = now;
        _baselineTimestamp = _monotonic.GetTimestamp();
    }

    // ---------------------------------------------------------------- scheduling

    private DateTime? ComputeNextPrompt(DateTime now)
    {
        if (!Tracking.PeriodicPromptsEnabled || _segmentId is null)
        {
            return null;
        }

        // Keep prompts at least half an interval apart (e.g. a start-up prompt at 9:28 skips the 9:30 slot).
        var interval = TimeSpan.FromMinutes(Tracking.CheckInIntervalMinutes);
        var after = _lastPromptUtc is { } last && last + (interval / 2) > now ? last + (interval / 2) : now;
        return CheckInSchedule.NextSlotUtc(after, Tracking.CheckInIntervalMinutes, _timeZone);
    }

    private void Reschedule()
    {
        if (_timer is null || _stopped)
        {
            return;
        }

        var now = Now;
        DateTime? wake = null;
        void Consider(DateTime? candidate)
        {
            if (candidate is { } c && (wake is null || c < wake))
            {
                wake = c;
            }
        }

        if (_segmentId is not null)
        {
            Consider(_nextPromptUtc);
            Consider(_extraPrompt?.DueUtc);
            Consider(_baselineWallUtc + HeartbeatInterval);
        }
        else if (_paused && Tracking.PausedUntilUtc is { } until)
        {
            Consider(until);
        }

        var due = wake is { } w ? (w > now ? w - now : TimeSpan.Zero) : Timeout.InfiniteTimeSpan;
        _timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // ---------------------------------------------------------------- settings

    private bool IsPausedBySettings(DateTime now) =>
        !Tracking.Enabled || (Tracking.PausedUntilUtc is { } until && until > now);

    private async Task SavePauseSettingsAsync(bool enabled, DateTime? pausedUntil, CancellationToken ct)
    {
        await _settings.UpdateAsync(
            s =>
            {
                s.Tracking.Enabled = enabled;
                s.Tracking.PausedUntilUtc = pausedUntil;
            },
            ct);
    }

    private void OnSettingsChanged(object? sender, WorkTraceSettings settings) =>
        _ = WithGateAsync(async ct =>
        {
            var now = Now;
            var shouldPause = IsPausedBySettings(now);
            if (shouldPause && !_paused)
            {
                _paused = true;
                await EndSegmentAsync(now, TrackingEndReason.Paused, ct);
            }
            else if (!shouldPause && _paused)
            {
                await ResumeCoreAsync(now, ct);
            }

            // Interval or prompt settings may have changed.
            _nextPromptUtc = ComputeNextPrompt(now);
            if (!Tracking.PeriodicPromptsEnabled)
            {
                _extraPrompt = null;
            }
        });

    // ---------------------------------------------------------------- plumbing

    private async Task WithGateAsync(Func<CancellationToken, Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_started || _stopped)
            {
                return;
            }

            await action(CancellationToken.None);
            AfterChange();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void AfterChange()
    {
        Reschedule();
        PublishStatus();
    }

    private void PublishStatus()
    {
        var state = _lastError is not null && _started && !_stopped ? TrackingState.Error
            : !_started || _stopped ? TrackingState.Stopped
            : _suspended ? TrackingState.Suspended
            : _paused ? TrackingState.Paused
            : TrackingState.Tracking;
        var status = new TrackingStatus(
            state,
            state == TrackingState.Paused ? Tracking.PausedUntilUtc : null,
            state == TrackingState.Tracking ? NextVisiblePrompt() : null,
            _locked,
            Tracking.PeriodicPromptsEnabled,
            Tracking.CheckInIntervalMinutes,
            _lastAttempt,
            _lastError);

        lock (_statusLock)
        {
            if (status == Status)
            {
                return;
            }

            Status = status;
        }

        StatusChanged?.Invoke(this, status);
    }

    private DateTime? NextVisiblePrompt() =>
        _extraPrompt is { } extra && (_nextPromptUtc is null || extra.DueUtc < _nextPromptUtc) ? extra.DueUtc : _nextPromptUtc;
}
