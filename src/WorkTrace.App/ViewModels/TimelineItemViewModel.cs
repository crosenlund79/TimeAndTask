using WorkTrace.Core.Tracking;

namespace WorkTrace.App.ViewModels;

/// <summary>A row in the Overview's "Today" list.</summary>
public sealed class TimelineItemViewModel(TimelineEvent item)
{
    public string Time { get; } = TimeZoneInfo.ConvertTimeFromUtc(item.AtUtc, TimeZoneInfo.Local).ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public string Title => item.Title;

    public string? Detail => item.Detail;

    public TimelineEventKind Kind => item.Kind;

    public bool IsCheckIn => item.Kind == TimelineEventKind.CheckIn;

    public bool IsUncertain => item.Kind is TimelineEventKind.MissedCheckIn or TimelineEventKind.DismissedCheckIn
        or TimelineEventKind.SuppressedCheckIn or TimelineEventKind.Locked;

    public string Glyph => item.Kind switch
    {
        TimelineEventKind.CheckIn => "",
        TimelineEventKind.MissedCheckIn or TimelineEventKind.DismissedCheckIn or TimelineEventKind.SuppressedCheckIn => "",
        TimelineEventKind.TrackingStarted => "",
        TimelineEventKind.TrackingStopped => "",
        TimelineEventKind.Locked => "",
        TimelineEventKind.Unlocked => "",
        _ => "",
    };
}
