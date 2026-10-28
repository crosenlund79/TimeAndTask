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

    /// <summary>Returns human-readable validation errors; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (General is null || Tracking is null || Screenshots is null || Reporting is null)
        {
            errors.Add("All settings groups must be present.");
            return errors;
        }

        Range(errors, "Tracking.CheckInIntervalMinutes", Tracking.CheckInIntervalMinutes, 5, 240);
        Range(errors, "Tracking.AutoDismissSeconds", Tracking.AutoDismissSeconds, 10, 3600);
        Range(errors, "Tracking.MaxEstimationHorizonMinutes", Tracking.MaxEstimationHorizonMinutes, 5, 24 * 60);
        Range(errors, "Tracking.LockGraceMinutes", Tracking.LockGraceMinutes, 0, 120);
        if (Tracking.MaxEstimationHorizonMinutes < Tracking.CheckInIntervalMinutes)
        {
            errors.Add("Tracking.MaxEstimationHorizonMinutes must be at least the check-in interval.");
        }

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
    public bool Enabled { get; set; } = true;

    public bool PeriodicPromptsEnabled { get; set; } = true;

    public int CheckInIntervalMinutes { get; set; } = 30;

    public PopupMode PopupMode { get; set; } = PopupMode.Unobtrusive;

    public PopupPosition PopupPosition { get; set; } = PopupPosition.BottomRight;

    public int AutoDismissSeconds { get; set; } = 120;

    public bool ReminderSound { get; set; }

    public bool RememberLastTask { get; set; } = true;

    /// <summary>Estimated continuation after the last observation never exceeds this horizon.</summary>
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

public sealed class ScreenshotSettings
{
    /// <summary>Screenshots require explicit opt-in.</summary>
    public bool Enabled { get; set; }

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
}
