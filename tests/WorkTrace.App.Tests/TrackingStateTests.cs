using WorkTrace.App.Tracking;
using WorkTrace.App.Tray;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App.Tests;

/// <summary>Pause/resume as shown in the overview and the tray menu; no check-in popup is involved.</summary>
public class TrackingStateTests
{
    [Fact]
    public Task PauseAndResume_UpdateOverviewAndTray() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        await ctx.Get<TrackingEngine>().StartAsync();
        var tracking = ctx.Get<TrackingController>();
        var home = ctx.Get<HomeViewModel>();
        var tray = ctx.Get<TrayIconService>();
        tray.Initialize();
        await WpfTestHost.SettleAsync();

        Assert.Same(tracking, home.Tracking);
        AssertActive();

        // Timed pause: shows the end time, offers Resume, then resumes by itself.
        await tracking.PauseCommand.ExecuteAsync("15");
        await WpfTestHost.SettleAsync();
        var until = TimeZoneInfo.ConvertTimeFromUtc(ctx.Time.GetUtcNow().UtcDateTime.AddMinutes(15), TimeZoneInfo.Local);
        Assert.True(tracking.IsPaused);
        Assert.Equal($"Paused until {until:HH:mm}", tracking.StatusText);
        Assert.Equal("No time is recorded while paused", tracking.StatusDetail);
        var menu = tray.MenuState;
        Assert.StartsWith($"Paused until {until:HH:mm}", menu.Status, StringComparison.Ordinal);
        Assert.False(menu.CanPause);
        Assert.True(menu.CanResume);

        ctx.Time.Advance(TimeSpan.FromMinutes(16));
        await WpfTestHost.SettleAsync();
        AssertActive();

        // Pause until resumed, then resume explicitly.
        await tracking.PauseCommand.ExecuteAsync("0");
        await WpfTestHost.SettleAsync();
        Assert.Equal("Paused", tracking.StatusText);
        Assert.True(tray.MenuState.CanResume);

        await tracking.ResumeCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync();
        AssertActive();

        Assert.Empty(UiTestContext.Popups());

        void AssertActive()
        {
            Assert.True(tracking.IsTracking);
            Assert.Equal("Active", tracking.StatusText);
            Assert.StartsWith("Next check-in at ", tracking.StatusDetail, StringComparison.Ordinal);
            Assert.Equal(TrackingState.Tracking, tracking.Status.State);
            var state = tray.MenuState;
            Assert.StartsWith("Active", state.Status, StringComparison.Ordinal);
            Assert.True(state.CanPause);
            Assert.False(state.CanResume);
            Assert.StartsWith("WorkTrace – Active", state.ToolTip, StringComparison.Ordinal);
        }
    });

    [Fact]
    public Task ShowNow_WhilePaused_ExplainsWhyNothingAppears() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        await ctx.Get<TrackingEngine>().StartAsync();
        var tracking = ctx.Get<TrackingController>();
        await tracking.PauseCommand.ExecuteAsync("0");

        await tracking.TriggerScheduledNowCommand.ExecuteAsync(null);
        await WpfTestHost.SettleAsync();

        Assert.Empty(UiTestContext.Popups());
        var notice = ctx.Get<WorkTrace.App.Shell.NotificationService>().Current;
        Assert.NotNull(notice);
        Assert.Contains("not active", notice.Message, StringComparison.Ordinal);
        Assert.Equal(CheckInAttemptResult.NotTracking, tracking.Status.LastAttempt?.Result);
        Assert.StartsWith("Last check-in ", tracking.LastAttemptText, StringComparison.Ordinal);
    });
}
