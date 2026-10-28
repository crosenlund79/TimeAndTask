using WorkTrace.Core.Entities;

namespace WorkTrace.Core.Tracking;

public enum TrackingState
{
    /// <summary>Not started yet (application starting).</summary>
    Stopped = 0,

    /// <summary>Tracking segments are recorded and check-ins are scheduled.</summary>
    Tracking = 1,

    /// <summary>Paused by the user, either until a time or until resumed.</summary>
    Paused = 2,

    /// <summary>The computer is asleep (between suspend and resume notifications).</summary>
    Suspended = 3,

    /// <summary>Tracking could not run (start-up or timer failure); see <see cref="TrackingStatus.LastError"/>.</summary>
    Error = 4,
}

/// <summary>What happened when a check-in came due (or was requested with "show now").</summary>
public enum CheckInAttemptResult
{
    /// <summary>The popup was shown and is waiting for an answer.</summary>
    Shown = 0,

    /// <summary>Not shown: a check-in popup was already open.</summary>
    SkippedPopupOpen = 1,

    /// <summary>Not shown: the Windows session is locked (stored as suppressed).</summary>
    SuppressedLocked = 2,

    /// <summary>Not shown: tracking is paused, asleep or stopped.</summary>
    NotTracking = 3,

    /// <summary>The popup could not be shown (see the log).</summary>
    Failed = 4,

    Answered = 5,
    Missed = 6,
    Dismissed = 7,
}

public sealed record CheckInAttempt(DateTime AtUtc, CheckInAttemptResult Result);

/// <summary>Snapshot of the tracking engine for the tray, popup and overview.</summary>
public sealed record TrackingStatus(
    TrackingState State,
    DateTime? PausedUntilUtc,
    DateTime? NextCheckInUtc,
    bool IsSessionLocked,
    bool PeriodicPromptsEnabled,
    int IntervalMinutes = 30,
    CheckInAttempt? LastAttempt = null,
    string? LastError = null)
{
    public static TrackingStatus Initial { get; } = new(TrackingState.Stopped, null, null, false, true);
}

/// <summary>Whether the Windows session is currently locked; null when it cannot be determined.</summary>
public interface ISessionStateProbe
{
    bool? IsSessionLocked();
}

/// <summary>How a shown check-in popup ended.</summary>
public enum PromptOutcome
{
    /// <summary>An observation was recorded (the check-in service already marked the prompt answered).</summary>
    Answered = 0,

    /// <summary>Closed by the user without answering (Escape / close button).</summary>
    Dismissed = 1,

    /// <summary>Closed automatically after the auto-dismiss timeout.</summary>
    TimedOut = 2,

    /// <summary>Closed because the application is shutting down.</summary>
    Closed = 3,
}

/// <summary>Why a check-in prompt is being shown (wording in the popup only).</summary>
public enum PromptReason
{
    Scheduled = 0,
    TrackingStarted = 1,
    Resumed = 2,
    WelcomeBack = 3,
}

/// <summary>Shows check-in popups. Implemented by the UI; must be safe to call from any thread.</summary>
public interface ICheckInPresenter
{
    /// <summary>True while a check-in popup (scheduled or manual) is open.</summary>
    bool IsOpen { get; }

    /// <summary>Shows the popup for a stored prompt and completes when it closes.</summary>
    Task<PromptOutcome> ShowPromptAsync(int promptId, DateTime scheduledAtUtc, PromptReason reason, CancellationToken cancellationToken);

    /// <summary>Closes any open popup (application exit).</summary>
    void CloseAll();
}

/// <summary>Persistence used by the tracking engine. All times are UTC.</summary>
public interface ITrackingStore
{
    /// <summary>
    /// Repairs state left by an unclean exit: open segments are closed at their last heartbeat
    /// (reason Crash), pending prompts become Missed and open lock periods are closed.
    /// Returns the number of repaired segments.
    /// </summary>
    Task<int> RecoverAsync(CancellationToken cancellationToken = default);

    Task<int> OpenSegmentAsync(DateTime startUtc, CancellationToken cancellationToken = default);

    Task CloseSegmentAsync(int segmentId, DateTime endUtc, TrackingEndReason reason, CancellationToken cancellationToken = default);

    Task HeartbeatAsync(int segmentId, DateTime atUtc, CancellationToken cancellationToken = default);

    Task<int> StartLockAsync(DateTime startUtc, CancellationToken cancellationToken = default);

    Task EndLockAsync(int lockId, DateTime endUtc, CancellationToken cancellationToken = default);

    Task<int> CreatePromptAsync(DateTime scheduledAtUtc, DateTime? shownAtUtc, CheckInOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Sets the outcome of a prompt that is still pending; resolved prompts are left alone.</summary>
    Task ResolvePromptAsync(int promptId, CheckInOutcome outcome, DateTime resolvedAtUtc, CancellationToken cancellationToken = default);
}
