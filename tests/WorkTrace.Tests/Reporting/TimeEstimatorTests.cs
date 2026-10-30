using WorkTrace.Core.Reporting;
using WorkTrace.Core.Settings;

namespace WorkTrace.Tests.Reporting;

public class TimeEstimatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int minutes) => T0.AddMinutes(minutes);

    private static EstimationInput Input(
        EstimationSegment[] segments,
        EstimationObservation[] observations,
        DateTime[]? prompts = null,
        EstimationSegment[]? locks = null,
        EstimationAllocation[]? allocations = null) =>
        new(segments, locks ?? [], observations, prompts ?? [], allocations ?? [], TimeSpan.FromMinutes(60), LockedTimeHandling.Unclassified, TimeSpan.Zero);

    private static EstimationObservation Obs(int id, int minutes, int? task) => new(id, At(minutes), At(minutes), task);

    private static string Describe(IEnumerable<TimelinePeriod> periods) =>
        string.Join(" | ", periods.Select(p => $"{(p.StartUtc - T0).TotalMinutes}-{(p.EndUtc - T0).TotalMinutes} {p.Kind}{(p.TaskId is { } t ? $" {t}" : "")}"));

    [Fact]
    public void SameTaskIsContinuous_DifferentTasksSwitchAtTheMidpoint_StartIsUnclassified_TrailingIsCapped()
    {
        var input = Input([new(At(0), At(300))], [Obs(1, 10, 1), Obs(2, 40, 1), Obs(3, 100, 2)]);

        var result = TimeEstimator.Estimate(input, At(0), At(300));

        Assert.Equal("0-10 Unclassified | 10-70 Estimated 1 | 70-160 Estimated 2 | 160-300 Unclassified", Describe(result));
    }

    [Fact]
    public void MissedPrompt_MakesTheTimeAfterItUnclassified_AndSegmentsAreNeverCrossed()
    {
        var input = Input(
            [new(At(0), At(60)), new(At(90), At(150))],
            [Obs(1, 0, 1), Obs(2, 30, 1), Obs(3, 100, 1)],
            prompts: [At(45)]);

        var result = TimeEstimator.Estimate(input, At(0), At(150));

        Assert.Equal("0-45 Estimated 1 | 45-60 Unclassified | 60-90 NotTracking | 90-100 Unclassified | 100-150 Estimated 1", Describe(result));
    }

    [Fact]
    public void LockedTime_IsUncertain_AndManualAllocationsWin()
    {
        var input = Input(
            [new(At(0), At(120))],
            [Obs(1, 0, 1), Obs(2, 60, 1)],
            locks: [new(At(20), At(40))],
            allocations: [new(1, At(30), At(50), At(500), 7, false)]);

        var result = TimeEstimator.Estimate(input, At(0), At(120));

        Assert.Equal("0-20 Estimated 1 | 20-30 Unclassified | 30-50 Manual 7 | 50-60 Unclassified | 60-120 Estimated 1", Describe(result));
        Assert.Equal(TimeSpan.FromMinutes(120), result.Aggregate(TimeSpan.Zero, (sum, p) => sum + p.Duration));
    }
}
