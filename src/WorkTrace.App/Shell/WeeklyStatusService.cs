using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Management;

namespace WorkTrace.App.Shell;

/// <summary>
/// "Copy status for next week": builds the Danish status message for the coming week from the planning timeline and puts it
/// on the clipboard as HTML (bold survives pasting into Outlook) with a plain-text fallback. Reads task data only.
/// </summary>
public sealed class WeeklyStatusService(ITaskService tasks, TimeProvider timeProvider, ILogger<WeeklyStatusService> logger)
{
    public async Task<WeeklyStatusReport> CopyAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local));
        var report = WeeklyStatus.Build(await tasks.SearchAsync(new TaskQuery(Limit: 500), cancellationToken), today);
        SetClipboard(CreateDataObject(report));
        logger.LogInformation(
            "Weekly status copied: week {Week}, {Tasks} tasks, {Planned} of {Available} min, {Provisional} min provisional",
            report.IsoWeek, report.Items.Count, report.PlannedMinutes, report.AvailableMinutes, report.ProvisionalMinutes);
        return report;
    }

    /// <summary>"Copied: uge 49, 20 t., belægning 53 %." for the confirmation.</summary>
    public static string Confirmation(WeeklyStatusReport report) =>
        $"Status for next week (uge {report.IsoWeek}) copied: {WeeklyStatus.Hours(report.PlannedMinutes)} h"
        + (report.WorkloadPercent is { } p ? $", belægning {p} %" : string.Empty)
        + ". Paste it into Outlook.";

    public static DataObject CreateDataObject(WeeklyStatusReport report)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, WeeklyStatus.ToClipboardHtml(WeeklyStatus.ToHtml(report)));
        data.SetData(DataFormats.UnicodeText, WeeklyStatus.ToPlainText(report));
        data.SetData(DataFormats.Text, WeeklyStatus.ToPlainText(report));
        return data;
    }

    /// <summary>Another program may hold the clipboard for a moment: try a few times.</summary>
    private static void SetClipboard(DataObject data)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(50);
            }
        }
    }
}
