using WorkTrace.Core.Reporting;
using WorkTrace.Core.Settings;

namespace WorkTrace.Tests.Reporting;

/// <summary>One continuous active interval per tracking segment (owner rules 2026-11-09).</summary>
public class TimeEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private static EstimationInput Input(
        EstimationSegment[] segments,
        EstimationObservation[] observations,
        EstimationSegment[]? locks = null,
        EstimationAllocation[]? allocations = null) =>
        new(segments, locks ?? [], observations, allocations ?? [], LockedTimeHandling.Unclassified, TimeSpan.Zero);

    private static EstimationObservation Obs(int id, int minutes, int? task, int? client = null) => new(id, At(minutes), At(minutes), task, client);

    private static string Describe(IEnumerable<TimelinePeriod> periods) =>
        string.Join(" | ", periods.Select(p => $"{(p.StartUtc - T0).TotalMinutes}-{(p.EndUtc - T0).TotalMinutes} {p.Kind}{(p.TaskId is { } t ? $" {t}" : "")}"));

    [Fact]
    public void OwnerExample_FirstSelectionClassifiesTheStartUp_SwitchesSplit_ExitCloses()
    {
        // 08:14 start · 09:14 A · 10:29 B · 11:05 A again · 16:00 exit (minutes after 08:00).
        var input = Input([new(At(14), At(480))], [Obs(1, 74, 1), Obs(2, 149, 2), Obs(3, 185, 1)]);

        var result = TimeEstimator.Estimate(input, At(14), At(480));

        Assert.Equal("14-149 Estimated 1 | 149-185 Estimated 2 | 185-480 Estimated 1", Describe(result));
    }

    [Fact]
    public void SameTaskAgain_Continues_AndElapsedTimeNeverEndsTheActiveTask()
    {
        var input = Input([new(At(0), At(600))], [Obs(1, 10, 1), Obs(2, 40, 1), Obs(3, 100, 2)]);

        var result = TimeEstimator.Estimate(input, At(0), At(600));

        Assert.Equal("0-100 Estimated 1 | 100-600 Estimated 2", Describe(result));
    }

    [Fact]
    public void NoteOnlyCheckIns_DoNotChangeTheActiveInterval_AndStakeholderSelectionsClassify()
    {
        var input = Input([new(At(0), At(120))], [Obs(1, 10, null), Obs(2, 30, null, client: 5), Obs(3, 60, null), Obs(4, 90, 1)]);

        var result = TimeEstimator.Estimate(input, At(0), At(120));

        Assert.Equal("0-90 Estimated | 90-120 Estimated 1", Describe(result));
        Assert.Equal(5, result[0].ClientId);
    }

    [Fact]
    public void Pauses_EndTheInterval_AndANewSegmentStartsUnclassified_UntilTheNextSelection()
    {
        // Paused 60–90: no time there; after the resume the time is unclassified until a task is chosen, then that task.
        var input = Input([new(At(0), At(60)), new(At(90), At(150))], [Obs(1, 0, 1), Obs(2, 100, 1)]);

        var result = TimeEstimator.Estimate(input, At(0), At(150));

        Assert.Equal("0-60 Estimated 1 | 60-90 NotTracking | 90-150 Estimated 1", Describe(result));

        var unanswered = TimeEstimator.Estimate(Input([new(At(0), At(60)), new(At(90), At(150))], [Obs(1, 0, 1)]), At(0), At(150));
        Assert.Equal("0-60 Estimated 1 | 60-90 NotTracking | 90-150 Unclassified", Describe(unanswered));
    }

    [Fact]
    public void LockedTime_FollowsTheSetting_TheTaskContinuesAfterwards_AndManualAllocationsWin()
    {
        var input = Input(
            [new(At(0), At(120))],
            [Obs(1, 0, 1)],
            locks: [new(At(20), At(40))],
            allocations: [new(1, At(70), At(80), At(500), 7, false)]);

        var result = TimeEstimator.Estimate(input, At(0), At(120));

        Assert.Equal("0-20 Estimated 1 | 20-40 Locked | 40-70 Estimated 1 | 70-80 Manual 7 | 80-120 Estimated 1", Describe(result));
        Assert.Equal(TimeSpan.FromMinutes(120), result.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Duration));
    }

    [Fact]
    public void ACheckInDuringALock_EndsTheLockThere()
    {
        // The unlock notification never came; the check-in at 50 proves the user was back.
        var input = Input([new(At(0), At(120))], [Obs(1, 0, 1), Obs(2, 50, 2)], locks: [new(At(20), At(120))]);

        Assert.Equal("0-20 Estimated 1 | 20-50 Locked | 50-120 Estimated 2", Describe(TimeEstimator.Estimate(input, At(0), At(120))));
    }

    [Fact]
    public void AfterSleepOrClockChange_TheActiveTaskContinues_ButNotAfterAPauseOrRestart()
    {
        EstimationSegment[] Segments(bool continues) => [new(At(0), At(60)), new(At(70), At(120), continues)];

        var afterSleep = TimeEstimator.Estimate(Input(Segments(continues: true), [Obs(1, 10, 1)]), At(0), At(120));
        Assert.Equal("0-60 Estimated 1 | 60-70 NotTracking | 70-120 Estimated 1", Describe(afterSleep)); // the gap is never work

        var afterPause = TimeEstimator.Estimate(Input(Segments(continues: false), [Obs(1, 10, 1)]), At(0), At(120));
        Assert.Equal("0-60 Estimated 1 | 60-70 NotTracking | 70-120 Unclassified", Describe(afterPause));
    }
}
