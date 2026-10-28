using WorkTrace.Core.Entities;

namespace WorkTrace.Tests.Core;

public class ObservationTests
{
    private static readonly DateTime T = new(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_CapturesOriginalValues()
    {
        var o = Observation.Create(T, T.AddSeconds(5), ObservationOrigin.Scheduled, taskId: 7, note: "  fixing invoice posting ");

        Assert.Equal(T, o.OriginalObservedAtUtc);
        Assert.Equal(7, o.OriginalTaskId);
        Assert.Equal("fixing invoice posting", o.Note);
        Assert.Equal(o.Note, o.OriginalNote);
        Assert.False(o.IsCorrected);
    }

    [Fact]
    public void Corrections_AreDistinguishableFromOriginal()
    {
        var o = Observation.Create(T, T, ObservationOrigin.Manual, taskId: 7);

        o.ObservedAtUtc = T.AddMinutes(-10);
        o.TaskId = 8;

        Assert.True(o.IsCorrected);
        Assert.Equal(T, o.OriginalObservedAtUtc);
        Assert.Equal(7, o.OriginalTaskId);
    }

    [Fact]
    public void SoftDelete_CountsAsCorrection()
    {
        var o = Observation.Create(T, T, ObservationOrigin.Manual, taskId: null);
        o.IsDeleted = true;
        Assert.True(o.IsCorrected);
    }

    [Fact]
    public void Create_RejectsUnspecifiedTimestamps()
    {
        var unspecified = DateTime.SpecifyKind(T, DateTimeKind.Unspecified);
        Assert.Throws<ArgumentException>(() => Observation.Create(unspecified, T, ObservationOrigin.Manual, null));
    }

    [Theory]
    [InlineData(CheckInOutcome.Answered, false)]
    [InlineData(CheckInOutcome.Pending, false)]
    [InlineData(CheckInOutcome.Missed, true)]
    [InlineData(CheckInOutcome.Dismissed, true)]
    [InlineData(CheckInOutcome.Suppressed, true)]
    public void CheckInOutcomes_ThatCreateUncertainty(CheckInOutcome outcome, bool expected)
    {
        Assert.Equal(expected, new CheckInPrompt { Outcome = outcome }.CreatesUncertainty);
    }
}
