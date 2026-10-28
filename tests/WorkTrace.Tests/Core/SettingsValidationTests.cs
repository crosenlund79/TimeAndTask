using WorkTrace.Core.Settings;

namespace WorkTrace.Tests.Core;

public class SettingsValidationTests
{
    [Fact]
    public void Defaults_AreValid_AndMatchSpecification()
    {
        var s = new WorkTraceSettings();

        Assert.Empty(s.Validate());
        Assert.Equal(30, s.Tracking.CheckInIntervalMinutes);
        Assert.False(s.Screenshots.Enabled);
        Assert.Equal(10, s.Screenshots.CaptureIntervalMinutes);
        Assert.Equal(14, s.Screenshots.RetentionDays);
        Assert.Equal(ScreenshotFormat.Png, s.Screenshots.Format);
        Assert.Equal(DayOfWeek.Monday, s.Reporting.WeekStart);
        Assert.Equal(0, s.Reporting.RoundingMinutes);
        Assert.Equal(LockedTimeHandling.Unclassified, s.Tracking.LockedTimeHandling);
        Assert.Equal(0, s.Tracking.LockGraceMinutes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(60)]
    public void Rounding_OnlyAllowsSupportedSteps(int minutes)
    {
        var s = new WorkTraceSettings();
        s.Reporting.RoundingMinutes = minutes;
        Assert.Equal(minutes == 0, s.Validate().Count == 0);
    }

    [Fact]
    public void InvalidValues_AreReported()
    {
        var s = new WorkTraceSettings();
        s.Tracking.CheckInIntervalMinutes = 0;
        s.Screenshots.JpegQuality = 0;
        s.Screenshots.Format = (ScreenshotFormat)42;

        var errors = s.Validate();

        Assert.Contains(errors, e => e.Contains("CheckInIntervalMinutes", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("JpegQuality", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Screenshots.Format", StringComparison.Ordinal));
    }

    [Fact]
    public void EstimationHorizon_MustCoverCheckInInterval()
    {
        var s = new WorkTraceSettings();
        s.Tracking.CheckInIntervalMinutes = 60;
        s.Tracking.MaxEstimationHorizonMinutes = 30;
        Assert.Contains(s.Validate(), e => e.Contains("MaxEstimationHorizonMinutes", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(37, true)]
    [InlineData(240, true)]
    [InlineData(0, false)]
    [InlineData(-5, false)]
    [InlineData(241, false)]
    public void Interval_AcceptsWholeMinutesFromOneTo240(int minutes, bool valid)
    {
        var s = new WorkTraceSettings();
        s.Tracking.CheckInIntervalMinutes = minutes;
        Assert.Equal(valid, !s.Validate().Any(e => e.Contains("CheckInIntervalMinutes", StringComparison.Ordinal)));
    }
}
