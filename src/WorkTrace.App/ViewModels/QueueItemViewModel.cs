using System.Globalization;
using System.Windows;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>A row of the planning timeline: one task as a block proportional to its (estimated or provisional) effort.</summary>
public sealed class QueueItemViewModel(QueueEntry entry, int totalMinutes)
{
    public QueueEntry Entry { get; } = entry;

    public int Id => Entry.Task.Id;

    public int Position => Entry.Position;

    public string Title => Entry.Task.Title;

    public string Context => Entry.Task.ExternalId is { Length: > 0 } ext ? $"{ext} · {Entry.Task.ClientName}" : Entry.Task.ClientName;

    public string StakeholderColor => Entry.Task.ClientColor;

    /// <summary>No estimate: shown red and dashed with a provisional block (never stored).</summary>
    public bool IsProvisional => Entry.IsProvisional;

    public string DurationText => IsProvisional ? "4 h? · no estimate" : TaskQueue.FormatDuration(Entry.DurationMinutes);

    public string StartText => Entry.StartMinutes == 0 ? "Next up" : "after " + TaskQueue.FormatDuration(Entry.StartMinutes);

    public string? PlannedText => Entry.Task.PlannedStart is { } d ? "planned " + d.ToString("d MMM", CultureInfo.CurrentCulture) : null;

    public bool IsWaiting => Entry.Task.IsWaiting;

    public GridLength Before { get; } = new(Math.Max(0, entry.StartMinutes), GridUnitType.Star);

    public GridLength Block { get; } = new(Math.Max(1, entry.DurationMinutes), GridUnitType.Star);

    public GridLength After { get; } = new(Math.Max(0, totalMinutes - entry.StartMinutes - entry.DurationMinutes), GridUnitType.Star);
}
