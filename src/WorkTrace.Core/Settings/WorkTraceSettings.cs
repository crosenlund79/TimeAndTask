namespace WorkTrace.Core.Settings;

/// <summary>
/// Machine-specific user settings, persisted as JSON outside the database so that restoring a
/// database backup never rolls back settings.
/// </summary>
public sealed class WorkTraceSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public GeneralSettings General { get; set; } = new();

    public TrackingSettings Tracking { get; set; } = new();

    public ScreenshotSettings Screenshots { get; set; } = new();

    public ReportingSettings Reporting { get; set; } = new();

    public BoardSettings Board { get; set; } = new();

    public ReminderSettings Reminders { get; set; } = new();

    public BackupSettings Backup { get; set; } = new();

    public TaskSettings Tasks { get; set; } = new();

    /// <summary>Deep copy, so editors can change settings without touching <see cref="ISettingsStore.Current"/>.</summary>
    public WorkTraceSettings Clone() =>
        System.Text.Json.JsonSerializer.Deserialize<WorkTraceSettings>(System.Text.Json.JsonSerializer.Serialize(this))!;

    /// <summary>Returns human-readable validation errors; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (General is null || Tracking is null || Screenshots is null || Reporting is null || Board is null || Reminders is null || Backup is null || Tasks is null)
        {
            errors.Add("All settings groups must be present.");
            return errors;
        }

        Range(errors, "Tracking.CheckInIntervalMinutes", Tracking.CheckInIntervalMinutes, TrackingSettings.MinIntervalMinutes, TrackingSettings.MaxIntervalMinutes);
        Range(errors, "Tracking.AutoDismissSeconds", Tracking.AutoDismissSeconds, 10, 3600);
        Range(errors, "Tracking.MaxEstimationHorizonMinutes", Tracking.MaxEstimationHorizonMinutes, 5, 24 * 60);
        Range(errors, "Tracking.LockGraceMinutes", Tracking.LockGraceMinutes, 0, 120);
        if (Tracking.MaxEstimationHorizonMinutes < Tracking.CheckInIntervalMinutes)
        {
            errors.Add("Tracking.MaxEstimationHorizonMinutes must be at least the check-in interval.");
        }

        Range(errors, "Tasks.DeadlineWarningDays", Tasks.DeadlineWarningDays, 0, 60);
        Range(errors, "Tasks.MaintenanceReminderMinutes", Tasks.MaintenanceReminderMinutes, 15, 24 * 60);
        Range(errors, "Backup.FolderKeep", Backup.FolderKeep, 1, 365);
        Range(errors, "Backup.EmailMaxMegabytes", Backup.EmailMaxMegabytes, 1, 35);
        Defined(errors, "Backup.Frequency", Backup.Frequency);
        Range(errors, "Reminders.IntervalMinutes", Reminders.IntervalMinutes, ReminderSettings.MinIntervalMinutes, ReminderSettings.MaxIntervalMinutes);
        Range(errors, "Screenshots.CaptureIntervalMinutes", Screenshots.CaptureIntervalMinutes, 1, 240);
        Range(errors, "Screenshots.RetentionDays", Screenshots.RetentionDays, 1, 3650);
        Range(errors, "Screenshots.JpegQuality", Screenshots.JpegQuality, 1, 100);
        Range(errors, "Screenshots.NotificationSeconds", Screenshots.NotificationSeconds, 0, 30);

        if (Reporting.RoundingMinutes is not (0 or 5 or 10 or 15 or 30))
        {
            errors.Add("Reporting.RoundingMinutes must be one of 0, 5, 10, 15, 30.");
        }

        Defined(errors, "Tracking.PopupMode", Tracking.PopupMode);
        Defined(errors, "Tracking.PopupPosition", Tracking.PopupPosition);
        Defined(errors, "Tracking.LockedTimeHandling", Tracking.LockedTimeHandling);
        Defined(errors, "Screenshots.Format", Screenshots.Format);
        Defined(errors, "Screenshots.Mode", Screenshots.Mode);
        Defined(errors, "General.Theme", General.Theme);
        Defined(errors, "Reporting.WeekStart", Reporting.WeekStart);

        return errors;
    }

    private static void Range(List<string> errors, string name, int value, int min, int max)
    {
        if (value < min || value > max)
        {
            errors.Add($"{name} must be between {min} and {max} (was {value}).");
        }
    }

    private static void Defined<TEnum>(List<string> errors, string name, TEnum value)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add($"{name} has an unknown value '{value}'.");
        }
    }
}

public enum ThemePreference
{
    System = 0,
    Light = 1,
    Dark = 2,
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    /// <summary>The navigation rail shows icons only (remembered between sessions).</summary>
    public bool NavigationCollapsed { get; set; }

    /// <summary>No longer used: WorkTrace has a single design (kept so existing settings files load unchanged).</summary>
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>Global shortcut for opening the check-in popup, e.g. "Ctrl+Alt+Space".</summary>
    public string GlobalShortcut { get; set; } = "Ctrl+Alt+Space";
}

public enum PopupMode
{
    /// <summary>Appear on top, activate and take keyboard focus.</summary>
    Focused = 0,

    /// <summary>Appear without stealing focus.</summary>
    Unobtrusive = 1,
}

public enum PopupPosition
{
    BottomRight = 0,
    TopRight = 1,
    Center = 2,
}

public enum LockedTimeHandling
{
    /// <summary>Locked time is uncertain (unclassified) and interrupts estimation. Default.</summary>
    Unclassified = 0,

    /// <summary>Locked time is treated as not working.</summary>
    NotWorking = 1,
}

public sealed class TrackingSettings
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 240;

    /// <summary>False while tracking is paused until the user resumes it.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When set (and in the future), tracking is paused until this time and then resumes by itself.</summary>
    public DateTime? PausedUntilUtc { get; set; }

    public bool PeriodicPromptsEnabled { get; set; } = true;

    /// <summary>Ask what you are working on shortly after tracking starts (app start, resume, unpause).</summary>
    public bool PromptOnStart { get; set; } = true;

    public int CheckInIntervalMinutes { get; set; } = 30;

    public PopupMode PopupMode { get; set; } = PopupMode.Unobtrusive;

    public PopupPosition PopupPosition { get; set; } = PopupPosition.BottomRight;

    public int AutoDismissSeconds { get; set; } = 120;

    public bool ReminderSound { get; set; }

    public bool RememberLastTask { get; set; } = true;

    /// <summary>Estimated continuation after the last observation never exceeds this horizon.</summary>
    /// <summary>No longer used: the chosen task runs until another task, a pause or exit (owner rules 2026-11-09). Kept so settings files stay valid.</summary>
    public int MaxEstimationHorizonMinutes { get; set; } = 60;

    public LockedTimeHandling LockedTimeHandling { get; set; } = LockedTimeHandling.Unclassified;

    /// <summary>
    /// Locks shorter than this are ignored by estimation. Default 0: every locked period is uncertain.
    /// </summary>
    public int LockGraceMinutes { get; set; }
}

public enum ScreenshotFormat
{
    Png = 0,
    Jpeg = 1,
}

public enum ScreenshotMode
{
    Off = 0,
    MainScreen = 1,
    AllScreens = 2,
}

public sealed class ScreenshotSettings
{
    /// <summary>Legacy flag (unused); <see cref="Mode"/> decides.</summary>
    public bool Enabled { get; set; }

    /// <summary>Automatic screenshots require explicit opt-in: Off by default.</summary>
    public ScreenshotMode Mode { get; set; } = ScreenshotMode.Off;

    public int CaptureIntervalMinutes { get; set; } = 10;

    public int RetentionDays { get; set; } = 14;

    public ScreenshotFormat Format { get; set; } = ScreenshotFormat.Png;

    public int JpegQuality { get; set; } = 85;

    public int NotificationSeconds { get; set; } = 2;
}

public sealed class ReportingSettings
{
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    /// <summary>0 = no rounding.</summary>
    public int RoundingMinutes { get; set; }

    /// <summary>Exports round each day's entry UP to the next 15 minutes; totals are summed from the rounded values. Off by default.</summary>
    public bool ExportRoundUpToQuarter { get; set; }
}

public sealed class BoardSettings
{
    /// <summary>
    /// Show the Board (navigation, tray) and create cards for tasks. Null until decided at start-up: off for a new
    /// installation, on for an existing one that already has cards. Turning it off never deletes cards.
    /// </summary>
    public bool? Enabled { get; set; }

    /// <summary>True unless the Board was switched off (an undecided value counts as on).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEnabled => Enabled != false;

    /// <summary>New tasks get a linked Board card (each task's "Show on Board" can still be switched off).</summary>
    public bool AutoAddTasks { get; set; } = true;
}

/// <summary>"Have you registered your time today?" reminders. The answer state lives here so it survives restarts.</summary>
public sealed class ReminderSettings
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 480;

    public bool Enabled { get; set; }

    /// <summary>Local time the reminder period starts; may be after <see cref="End"/> (period crosses midnight).</summary>
    public TimeOnly Start { get; set; } = new(15, 0);

    public TimeOnly End { get; set; } = new(8, 0);

    public int IntervalMinutes { get; set; } = 60;

    public bool PlaySound { get; set; }

    /// <summary>Best-effort reminder when Windows shuts down or signs out (shown at the next start, see docs).</summary>
    public bool OnShutdown { get; set; }

    /// <summary>Working day (the date its reminder period started) already answered "done" or "remind me tomorrow".</summary>
    public DateOnly? DoneForDay { get; set; }

    /// <summary>No reminder before this time (set when one is shown or answered "Not yet").</summary>
    public DateTime? NotBeforeUtc { get; set; }

    /// <summary>Windows shut down on this day while time registration was outstanding: ask at the next start.</summary>
    public DateOnly? AskAfterShutdownForDay { get; set; }
}

public enum BackupFrequency
{
    Daily = 0,
    Weekly = 1,
}

/// <summary>
/// Automatic backups to a folder and/or by email (each optional). Status fields are written by the backup job.
/// The email API key is not stored here: it lives in Windows Credential Manager.
/// </summary>
public sealed class BackupSettings
{
    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;

    // ---- Folder ----
    public bool FolderEnabled { get; set; }

    /// <summary>Local, network or cloud-synced folder (e.g. OneDrive).</summary>
    public string? Folder { get; set; }

    /// <summary>Complete backups kept in the folder (older ones are deleted).</summary>
    public int FolderKeep { get; set; } = 14;

    public bool FolderIncludeScreenshots { get; set; } = true;

    public DateTime? LastFolderUtc { get; set; }

    public string? LastFolderResult { get; set; }

    public string? LastFolderError { get; set; }

    public string? LastFolderFingerprint { get; set; }

    // ---- Email ----
    public bool EmailEnabled { get; set; }

    public string? EmailTo { get; set; }

    /// <summary>Sender; Resend's test sender works without a domain, but only to the Resend account's own address.</summary>
    public string EmailFrom { get; set; } = "WorkTrace <onboarding@resend.dev>";

    /// <summary>Largest attachment per email; bigger backups are split into several emails.</summary>
    public int EmailMaxMegabytes { get; set; } = 20;

    public DateTime? LastEmailUtc { get; set; }

    public string? LastEmailResult { get; set; }

    public string? LastEmailError { get; set; }

    public string? LastEmailFingerprint { get; set; }
}

public sealed class TaskSettings
{
    /// <summary>Deadline warnings (bell, thicker card border) start this many days before the deadline.</summary>
    public int DeadlineWarningDays { get; set; } = 3;

    /// <summary>"Have you kept your Tasks and Board up to date?" after this long without using WorkTrace.</summary>
    public bool MaintenanceReminderEnabled { get; set; } = true;

    public int MaintenanceReminderMinutes { get; set; } = 120;

    /// <summary>Daily "review your upcoming tasks" popup with the active pipeline (off by default).</summary>
    public bool PlanningReminderEnabled { get; set; }

    /// <summary>Local time of the planning reminder (shown once per day, at or after this time).</summary>
    public TimeOnly PlanningReminderTime { get; set; } = new(9, 0);

    public bool PlanningReminderWeekdaysOnly { get; set; } = true;

    /// <summary>Day the planning reminder was last shown (so it comes once a day, also across restarts).</summary>
    public DateOnly? PlanningReminderShownOn { get; set; }

    /// <summary>Task-list groups the user collapsed (remembered between sessions).</summary>
    public List<Management.TaskGroup> CollapsedGroups { get; set; } = [];
}
