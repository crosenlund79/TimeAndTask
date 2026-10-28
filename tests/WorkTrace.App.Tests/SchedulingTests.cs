using System.Diagnostics;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.App.Tests;

/// <summary>
/// The tracking engine wired to the real presenter and the real system clock (no fake time): the timer
/// actually fires, the popup is created on the UI thread and its outcome is written back.
/// </summary>
public class SchedulingTests
{
    [Fact]
    public Task RealTimer_OneMinuteInterval_ShowsScheduledPopup_WhileHiddenInTray_AndRecordsMissed() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync(realTime: true);
        await ConfigureAsync(ctx, PopupMode.Unobtrusive);
        var engine = ctx.Get<TrackingEngine>();
        await engine.StartAsync();

        Assert.Equal(TrackingState.Tracking, engine.Status.State);
        Assert.NotNull(engine.Status.NextCheckInUtc);
        Assert.Equal(1, engine.Status.IntervalMinutes);

        // No main window at all: the app is "in the tray".
        var popup = await WaitForPopupAsync(TimeSpan.FromSeconds(80));
        Assert.True(popup is not null, $"No scheduled popup within 80 s; status: {engine.Status}");
        var vm = (CheckInViewModel)popup.DataContext;
        Assert.True(vm.IsScheduled);
        Assert.True(vm.IsCountdownActive);
        Assert.False(popup.IsActive, "An unobtrusive scheduled popup must not take focus");
        Assert.Equal(CheckInAttemptResult.Shown, engine.Status.LastAttempt?.Result);

        // Unanswered: closes after the countdown and is recorded as missed.
        await WaitUntilAsync(() => !popup.IsVisible, TimeSpan.FromSeconds(30));
        Assert.False(popup.IsVisible, "The popup did not close after its countdown");
        await WaitUntilAsync(() => engine.Status.LastAttempt?.Result == CheckInAttemptResult.Missed, TimeSpan.FromSeconds(10));
        Assert.Equal(CheckInAttemptResult.Missed, engine.Status.LastAttempt?.Result);
        Assert.Equal(TrackingState.Tracking, engine.Status.State);
        Assert.NotNull(engine.Status.NextCheckInUtc);
    });

    [Fact]
    public Task ShowNow_OpensARealScheduledPopup_AndReportsWhenItCannot() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync(realTime: true);
        await ConfigureAsync(ctx, PopupMode.Focused, interval: 60);
        var engine = ctx.Get<TrackingEngine>();
        await engine.StartAsync();

        var before = UiTestContext.Popups();
        Assert.Equal(CheckInAttemptResult.Shown, await engine.TriggerCheckInNowAsync());
        await WpfTestHost.SettleAsync(10);
        var opened = UiTestContext.Popups().Except(before).ToList();
        Assert.True(opened.Count == 1, $"Expected one popup, found {opened.Count}: {UiTestContext.Describe(opened)}");
        var vm = (CheckInViewModel)opened[0].DataContext;
        Assert.True(vm.IsScheduled);
        Assert.True(vm.IsCountdownActive);

        // A popup is already open: say so instead of silently doing nothing.
        Assert.Equal(CheckInAttemptResult.SkippedPopupOpen, await engine.TriggerCheckInNowAsync());

        vm.DismissCommand.Execute(null);
        await WaitUntilAsync(() => engine.Status.LastAttempt?.Result == CheckInAttemptResult.Dismissed, TimeSpan.FromSeconds(5));
        Assert.Equal(CheckInAttemptResult.Dismissed, engine.Status.LastAttempt?.Result);

        await engine.PauseAsync(null);
        Assert.Equal(CheckInAttemptResult.NotTracking, await engine.TriggerCheckInNowAsync());
        Assert.Empty(UiTestContext.Popups().Except(before));

        await engine.ResumeAsync();
        Assert.Equal(TrackingState.Tracking, engine.Status.State);
        Assert.Equal(CheckInAttemptResult.Shown, await engine.TriggerCheckInNowAsync());
        await WpfTestHost.SettleAsync(10);
        Assert.Single(UiTestContext.Popups().Except(before));
    });

    private static async Task ConfigureAsync(UiTestContext ctx, PopupMode mode, int interval = 1) =>
        await ctx.Get<ISettingsStore>().UpdateAsync(s =>
        {
            s.Tracking.CheckInIntervalMinutes = interval;
            s.Tracking.PromptOnStart = false;
            s.Tracking.AutoDismissSeconds = 10;
            s.Tracking.PopupMode = mode;
        });

    private static async Task<CheckInWindow?> WaitForPopupAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            if (UiTestContext.Popups().FirstOrDefault(p => p.IsVisible) is { } popup)
            {
                return popup;
            }

            await WpfTestHost.SettleAsync(5);
        }

        return null;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!condition() && clock.Elapsed < timeout)
        {
            await WpfTestHost.SettleAsync(3);
        }
    }
}
