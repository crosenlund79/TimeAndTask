namespace WorkTrace.Core.Management;

/// <summary>
/// The planning timeline as shown (same filter, order and days), ready to export. <paramref name="FilterName"/> is the
/// timeline filter's label ("All active", "Waiting", …).
/// </summary>
public sealed record TimelineExportRequest(PipelinePlan Plan, string FilterName, DateTime GeneratedAtLocal);

/// <summary>Writes the planning timeline to local files (Excel workbook, printable PDF). Never changes task data.</summary>
public interface ITimelineExporter
{
    Task ExportExcelAsync(TimelineExportRequest request, string path, CancellationToken cancellationToken = default);

    Task ExportPdfAsync(TimelineExportRequest request, string path, CancellationToken cancellationToken = default);
}

/// <summary>Shared timeline facts for both export formats, so Excel and PDF show the same planning information.</summary>
public static class TimelineLayout
{
    /// <summary>Planned minutes of the entry on each day of <paramref name="days"/> (public holidays get none). Sums to the entry's effort.</summary>
    public static int[] MinutesPerDay(QueueEntry entry, IReadOnlyList<PlanDay> days)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(days);
        var result = new int[days.Count];
        for (var i = 0; i < days.Count; i++)
        {
            if (days[i].CapacityMinutes == 0)
            {
                continue;
            }

            var overlap = Math.Min(entry.EndDay, i + 1) - Math.Max(entry.StartDay, i);
            if (overlap > 0)
            {
                result[i] = (int)Math.Round(overlap * TaskPipeline.WorkingDayMinutes, MidpointRounding.AwayFromZero);
            }
        }

        return result;
    }

    /// <summary>"Regular", "Waiting (reason)", "Recurring", "Closed".</summary>
    public static string Status(TaskSummary task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Group switch
        {
            TaskGroup.Waiting when !string.IsNullOrWhiteSpace(task.WaitingReason) => $"Waiting ({task.WaitingReason})",
            var g => g.ToString(),
        };
    }

    public static string DeadlineStatus(TaskSummary task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Deadline is null ? "No deadline" : task.IsClosed ? "Closed" : task.DeadlineState switch
        {
            DeadlineState.Overdue => "Overdue",
            DeadlineState.Soon => "Due soon",
            _ => "On track",
        };
    }

    /// <summary>"BUG-42 · Invoice posting error", or the title alone.</summary>
    public static string TaskLabel(TaskSummary task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return string.IsNullOrWhiteSpace(task.ExternalId) ? task.Title : $"{task.ExternalId} · {task.Title}";
    }

    public static string EffortText(QueueEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.IsProvisional ? "4 h? (no estimate)" : TaskPipeline.FormatDuration(entry.DurationMinutes);
    }

    /// <summary>"#RRGGBB" (or "#AARRGGBB") → "RRGGBB"; anything else → the neutral stakeholder grey.</summary>
    public static string Rgb(string? color)
    {
        var hex = (color ?? "").TrimStart('#');
        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? hex.ToUpperInvariant() : "7A7F87";
    }
}
