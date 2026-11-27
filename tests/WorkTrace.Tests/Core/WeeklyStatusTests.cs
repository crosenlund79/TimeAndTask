using System.Text;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.Tests.Core;

public class WeeklyStatusTests
{
    private static TaskSummary Task(int id, string client, int? minutes, string? system = null, string? externalId = null,
        bool waiting = false, bool closed = false) => new(
        id, client.Length, client, true, null, system, externalId, $"Opgave {id}", null, null, null, false, DateTime.UnixEpoch, DateTime.UnixEpoch, null,
        IsWaiting: waiting, IsClosed: closed, EstimatedRemainingMinutes: minutes);

    [Fact]
    public void NextWeek_TakesEachTasksShareOfTheWeek_FromThePlanningTimeline()
    {
        // Friday 27 Nov 2026: the timeline plans from today, so 7.5 h of task 1 fall on Friday and 2.5 h in week 49.
        var report = WeeklyStatus.Build(
            [
                Task(1, "Esvagt", 600, "DevOps", "4711"),
                Task(2, "Esvagt", null),                         // no estimate: provisional 4 h block
                Task(3, "BioGas", 1200, "CRM", "9A478091990"),
                Task(4, "BioGas", 60, waiting: true),            // waiting without a planned start: not reported
                Task(5, "BioGas", 60, closed: true),
            ],
            new DateOnly(2026, 11, 27));

        Assert.Equal(new DateOnly(2026, 11, 30), report.WeekStart);
        Assert.Equal([150, 240, 1200], report.Items.Select(i => i.Minutes));
        Assert.Equal(1590, report.PlannedMinutes);
        Assert.Equal(2250, report.AvailableMinutes);                 // 5 × 7.5 h
        Assert.Equal(71, report.WorkloadPercent);                   // 26.5 / 37.5
        Assert.False(report.MostlyProvisional);

        var text = WeeklyStatus.ToPlainText(report);
        Assert.StartsWith("Hej Bruno,", text, StringComparison.Ordinal);
        Assert.Contains("Status for den kommende uge (uge 49):", text, StringComparison.Ordinal);
        Assert.Contains("Mit konkrete forventede timetal er 26,5;", text, StringComparison.Ordinal);
        Assert.Contains("Kunde \"Esvagt\":\r\nDevOps#4711 Opgave 1\r\nEstimat: 2,5 t.\r\nOpgave 2\r\nEstimat: 4 t. *", text, StringComparison.Ordinal);
        Assert.Contains("CRM#9A478091990 Opgave 3\r\nEstimat: 20 t.\r\n", text, StringComparison.Ordinal);
        Assert.Contains("* Foreløbige estimater", text, StringComparison.Ordinal);
        Assert.Contains("min belægning er ca. 71 %.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Opgave 4", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Opgave 5", text, StringComparison.Ordinal);
        Assert.DoesNotContain("En del af estimaterne", text, StringComparison.Ordinal);
        Assert.EndsWith("Ha' en god weekend\r\n", text, StringComparison.Ordinal);

        var html = WeeklyStatus.ToHtml(report);
        Assert.Contains("ca. <b>71 %</b>.", html, StringComparison.Ordinal);
        Assert.Contains("<b>Kunde &quot;Esvagt&quot;:</b>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HolidayWeek_HasFewerAvailableHours_AndMostlyProvisionalPlansSayLessIsKnown()
    {
        // Week 52 of 2026: Juledag (Friday 25 Dec) is a public holiday; Juleaftensdag is customary and still a working day.
        var report = WeeklyStatus.Build([Task(1, "BioGas", null)], new DateOnly(2026, 12, 19)); // Saturday: planning starts on Monday

        Assert.Equal(1800, report.AvailableMinutes);
        Assert.Equal(13, report.WorkloadPercent);                   // 4 / 30
        Assert.True(report.MostlyProvisional);
        var text = WeeklyStatus.ToPlainText(report);
        Assert.Contains("Ugen har 30 arbejdstimer pga. juledag (25. december).", text, StringComparison.Ordinal);
        Assert.Contains("En del af estimaterne er foreløbige, og jeg arbejder på at få opgaverne nærmere afklaret.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoPlannedWork_SaysSo_WithZeroWorkload()
    {
        var text = WeeklyStatus.ToPlainText(WeeklyStatus.Build([], new DateOnly(2026, 11, 25)));

        Assert.Contains("Jeg har ingen kendte opgaver planlagt for den kommende uge.", text, StringComparison.Ordinal);
        Assert.Contains("ca. 0 %", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreløbige", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardHtml_OffsetsPointAtTheFragment_InUtf8Bytes()
    {
        const string fragment = "<p>Belægning <b>53 %</b> – Ha' en god weekend</p>";
        var clip = WeeklyStatus.ToClipboardHtml(fragment);
        var bytes = Encoding.UTF8.GetBytes(clip);
        int Offset(string name) => int.Parse(clip.Split("\r\n").Single(l => l.StartsWith(name + ":", StringComparison.Ordinal))[(name.Length + 1)..],
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, Offset("StartFragment"), Offset("EndFragment") - Offset("StartFragment")));
        Assert.StartsWith("<html>", Encoding.UTF8.GetString(bytes, Offset("StartHTML"), 6), StringComparison.Ordinal);
        Assert.Equal(bytes.Length, Offset("EndHTML"));
    }

    [Theory]
    [InlineData("2026-11-27T14:29", null, false)]
    [InlineData("2026-11-27T14:30", null, true)]
    [InlineData("2026-11-28T09:00", null, true)]                  // missed on Friday: still due at the weekend
    [InlineData("2026-11-28T09:00", "2026-11-23", false)]          // "Done for this week"
    [InlineData("2026-11-30T15:00", "2026-11-23", false)]          // next week, before its Friday
    public void WeeklyReminder_IsDueFromItsDayAndTime_UntilDoneForTheWeek(string local, string? doneWeek, bool due)
    {
        var settings = new ReminderSettings
        {
            WeeklyReportEnabled = true,
            WeeklyReportDoneWeek = doneWeek is null ? null : DateOnly.Parse(doneWeek, System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(due, WeeklyReportReminder.IsDue(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), settings));
    }
}
