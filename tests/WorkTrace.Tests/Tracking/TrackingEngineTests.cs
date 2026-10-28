using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Tracking;
using Microsoft.Extensions.DependencyInjection;

namespace WorkTrace.Tests.Tracking;

public class TrackingEngineTests : TrackingTestBase
{
    private async Task StartWithoutStartupPromptAsync()
    {
        await ConfigureAsync(t => t.PromptOnStart = false);
        await StartEngineAsync();
    }

    [Fact]
    public async Task Start_OpensSegment_AndSchedulesNextAlignedSlot()
    {
        await StartWithoutStartupPromptAsync();

        var segment = Assert.Single(await SegmentsAsync());
        Assert.Equal(Start, segment.StartUtc);
        Assert.Null(segment.EndUtc);
        Assert.Equal(TrackingState.Tracking, Engine.Status.State);
        Assert.Equal(At(9, 30), Engine.Status.NextCheckInUtc);
    }

    [Fact]
    public async Task StartupPrompt_IsShownShortlyAfterStart_AndSkipsAnImmediatelyFollowingSlot()
    {
        Clock.SetUtcNow(At(9, 25));
        await StartEngineAsync();

        await AdvanceAsync(TimeSpan.FromSeconds(30));
        var (promptId, scheduled, reason) = Assert.Single(Presenter.Shown);
        Assert.Equal(PromptReason.TrackingStarted, reason);
        Presenter.Complete(PromptOutcome.TimedOut);
        await Engine.IdleAsync();

        // 09:30 is too close to the start-up prompt; the next prompt is at 10:00.
        Assert.Equal(At(10, 0), Engine.Status.NextCheckInUtc);
        await AdvanceToAsync(9, 59);
        Assert.Single(Presenter.Shown);
    }

    [Fact]
    public async Task UnansweredPrompt_IsStoredAsMissed_FromItsScheduledTime()
    {
        await StartWithoutStartupPromptAsync();

        await AdvanceToAsync(9, 30, 5);
        var shown = Assert.Single(Presenter.Shown);
        Assert.Equal(At(9, 30), shown.ScheduledUtc);

        await AdvanceAsync(TimeSpan.FromMinutes(2));
        Presenter.Complete(PromptOutcome.TimedOut);
        await Engine.IdleAsync();

        var prompt = Assert.Single(await PromptsAsync());
        Assert.Equal(CheckInOutcome.Missed, prompt.Outcome);
        Assert.Equal(At(9, 30), prompt.ScheduledAtUtc);
        Assert.True(prompt.CreatesUncertainty);
        Assert.Equal(At(10, 0), Engine.Status.NextCheckInUtc);
    }

    [Fact]
    public async Task DismissedPrompt_IsStoredAsDismissed()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5);

        Presenter.Complete(PromptOutcome.Dismissed);
        await Engine.IdleAsync();

        Assert.Equal(CheckInOutcome.Dismissed, Assert.Single(await PromptsAsync()).Outcome);
    }

    [Fact]
    public async Task AnsweredPrompt_LinksTheObservation()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5);
        var promptId = Assert.Single(Presenter.Shown).PromptId;

        var observation = await CheckIns.RecordAsync(new CheckInRequest(promptId, null, "Reviewing pull requests"), Ct);
        Presenter.Complete(PromptOutcome.Answered);
        await Engine.IdleAsync();

        var prompt = Assert.Single(await PromptsAsync());
        Assert.Equal(CheckInOutcome.Answered, prompt.Outcome);
        Assert.Equal(observation.Id, prompt.ObservationId);
    }

    [Fact]
    public async Task OpenPopup_PreventsDuplicatePrompts()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5);

        // The first popup is still open when the next slot arrives.
        await AdvanceToAsync(10, 0, 5);

        Assert.Single(Presenter.Shown);
        Assert.Single(await PromptsAsync());
    }

    [Fact]
    public async Task SleepEndsTheSegment_AndNoBacklogIsShownAfterResume()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 40);
        Presenter.Complete(PromptOutcome.TimedOut); // the 09:30 check-in
        await ConfigureAsync(t => t.PromptOnStart = true); // ask once when the computer wakes up
        await Engine.IdleAsync();

        await Engine.NotifySuspendAsync();
        Assert.Equal(TrackingState.Suspended, Engine.Status.State);
        await AdvanceAsync(TimeSpan.FromHours(3)); // 12:40 – six slots passed while asleep
        await Engine.NotifyResumeAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(30));

        var segments = await SegmentsAsync();
        Assert.Equal(2, segments.Count);
        Assert.Equal(At(9, 40), segments[0].EndUtc);
        Assert.Equal(TrackingEndReason.Sleep, segments[0].EndReason);
        Assert.Equal(At(12, 40), segments[1].StartUtc);

        // 09:30 before sleeping, then exactly one prompt after resume. Nothing for 10:00 … 12:30.
        Assert.Equal([PromptReason.Scheduled, PromptReason.Resumed], Presenter.Shown.Select(s => s.Reason));
        Assert.Equal(2, (await PromptsAsync()).Count);
    }

    [Fact]
    public async Task LockedSession_RecordsLockPeriod_SuppressesPrompts_AndAsksOnceAfterUnlock()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 20);
        await Engine.NotifySessionLockedAsync();

        await AdvanceToAsync(10, 10); // 09:30 and 10:00 come due while locked
        Assert.Empty(Presenter.Shown);

        await Engine.NotifySessionUnlockedAsync();
        await AdvanceAsync(TimeSpan.FromSeconds(10));

        var lockPeriod = Assert.Single(await LocksAsync());
        Assert.Equal(At(9, 20), lockPeriod.StartUtc);
        Assert.Equal(At(10, 10), lockPeriod.EndUtc);

        var prompts = await PromptsAsync();
        Assert.Equal([CheckInOutcome.Suppressed, CheckInOutcome.Suppressed, CheckInOutcome.Pending], prompts.Select(p => p.Outcome));
        Assert.Equal(PromptReason.WelcomeBack, Assert.Single(Presenter.Shown).Reason);
    }

    [Fact]
    public async Task TimedPause_EndsSegment_PersistsAndResumesByItself()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 10);

        await Engine.PauseAsync(TimeSpan.FromHours(1));

        Assert.Equal(TrackingState.Paused, Engine.Status.State);
        Assert.Equal(At(10, 10), Engine.Status.PausedUntilUtc);
        Assert.Equal(At(10, 10), Settings.Current.Tracking.PausedUntilUtc);
        var paused = Assert.Single(await SegmentsAsync());
        Assert.Equal(TrackingEndReason.Paused, paused.EndReason);

        await AdvanceToAsync(9, 45);
        Assert.Empty(Presenter.Shown); // no check-ins while paused

        await AdvanceToAsync(10, 10, 10);
        Assert.Equal(TrackingState.Tracking, Engine.Status.State);
        Assert.Null(Settings.Current.Tracking.PausedUntilUtc);
        var segments = await SegmentsAsync();
        Assert.Equal(2, segments.Count);
        Assert.Equal(At(10, 10), segments[1].StartUtc);
    }

    [Fact]
    public async Task IndefinitePause_SurvivesRestart_UntilResumed()
    {
        await StartWithoutStartupPromptAsync();
        await Engine.PauseAsync(null);
        Assert.False(Settings.Current.Tracking.Enabled);

        await RestartEngineAsync();
        Assert.Equal(TrackingState.Paused, Engine.Status.State);
        Assert.Single(await SegmentsAsync()); // no segment opened while paused

        await Engine.ResumeAsync();
        Assert.True(Settings.Current.Tracking.Enabled);
        Assert.Equal(TrackingState.Tracking, Engine.Status.State);
        Assert.Equal(2, (await SegmentsAsync()).Count);
    }

    [Fact]
    public async Task UncleanExit_IsRecoveredAtLastHeartbeat()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5); // prompt pending
        await AdvanceToAsync(9, 36);    // heartbeat at ~09:12, 09:17 … 09:32

        // Simulate a crash: the engine is abandoned without StopAsync, then the app starts again.
        var abandoned = Engine;
        // (A crashed process runs no more timers, so the clock is not advanced before the restart.)
        var heartbeat = Assert.Single(await SegmentsAsync()).LastHeartbeatUtc;
        await StartEngineAsync();

        var segments = await SegmentsAsync();
        Assert.Equal(TrackingEndReason.Crash, segments[0].EndReason);
        Assert.Equal(heartbeat, segments[0].EndUtc);
        Assert.True(heartbeat >= At(9, 32));
        Assert.Equal(CheckInOutcome.Missed, (await PromptsAsync())[0].Outcome);
        Assert.Null(segments[1].EndUtc);
        GC.KeepAlive(abandoned);
    }

    [Fact]
    public async Task ClockJump_SplitsTheSegment()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 20);

        // The system clock jumps two hours ahead while real elapsed time does not.
        Clock.Advance(TimeSpan.FromHours(2));
        await Engine.NotifyTimeChangedAsync();

        var segments = await SegmentsAsync();
        Assert.Equal(2, segments.Count);
        Assert.Equal(TrackingEndReason.ClockChange, segments[0].EndReason);
        Assert.Equal(At(9, 20), segments[0].EndUtc);
        Assert.Equal(At(11, 20), segments[1].StartUtc);
        Assert.Empty(Presenter.Shown); // no burst of prompts for the skipped slots
    }

    [Fact]
    public async Task Stop_ClosesSegment_AndOpenPromptBecomesMissed()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5);

        await Engine.StopAsync();
        await Engine.IdleAsync();

        var segment = Assert.Single(await SegmentsAsync());
        Assert.Equal(TrackingEndReason.Shutdown, segment.EndReason);
        Assert.Equal(At(9, 30, 5), segment.EndUtc);
        Assert.Equal(CheckInOutcome.Missed, Assert.Single(await PromptsAsync()).Outcome);
    }

    [Fact]
    public async Task ChangingTheInterval_ReschedulesImmediately()
    {
        await StartWithoutStartupPromptAsync();
        Assert.Equal(At(9, 30), Engine.Status.NextCheckInUtc);

        await ConfigureAsync(t => t.CheckInIntervalMinutes = 15);
        await Engine.IdleAsync();

        Assert.Equal(At(9, 15), Engine.Status.NextCheckInUtc);
    }

    [Fact]
    public async Task DisabledPeriodicPrompts_KeepTracking_WithoutPrompts()
    {
        await ConfigureAsync(t => t.PeriodicPromptsEnabled = false);
        await StartEngineAsync();

        await AdvanceToAsync(10, 5);

        Assert.Equal(TrackingState.Tracking, Engine.Status.State);
        Assert.Null(Engine.Status.NextCheckInUtc);
        Assert.Empty(Presenter.Shown);
        Assert.Null(Assert.Single(await SegmentsAsync()).EndUtc);
    }

    [Fact]
    public async Task Heartbeat_IsWrittenPeriodically()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceAsync(TimeSpan.FromMinutes(11));

        var segment = Assert.Single(await SegmentsAsync());
        Assert.True(segment.LastHeartbeatUtc >= Start.AddMinutes(10));
    }

    [Fact]
    public async Task PauseFromSettingsPage_IsApplied()
    {
        await StartWithoutStartupPromptAsync();

        await ConfigureAsync(t => t.Enabled = false);
        await Engine.IdleAsync();

        Assert.Equal(TrackingState.Paused, Engine.Status.State);
        Assert.Equal(TrackingEndReason.Paused, Assert.Single(await SegmentsAsync()).EndReason);
    }

    [Fact]
    public async Task OneMinuteInterval_PromptsEveryMinute_WithoutDuplicates()
    {
        await ConfigureAsync(t =>
        {
            t.PromptOnStart = false;
            t.CheckInIntervalMinutes = 1;
        });
        await StartEngineAsync();
        Assert.Equal(At(9, 8), Engine.Status.NextCheckInUtc);

        for (var minute = 8; minute <= 10; minute++)
        {
            await AdvanceToAsync(9, minute, 5);
            Presenter.Complete(PromptOutcome.TimedOut);
            await Engine.IdleAsync();
        }

        Assert.Equal([At(9, 8), At(9, 9), At(9, 10)], Presenter.Shown.Select(s => s.ScheduledUtc));
        Assert.Equal(3, (await PromptsAsync()).Count);
    }

    [Fact]
    public async Task ShorterInterval_TakesEffectImmediately_WithoutBacklog()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 20);

        await ConfigureAsync(t => t.CheckInIntervalMinutes = 1);
        await Engine.IdleAsync();

        // The slots 09:08 … 09:20 that "would have" existed are not replayed: the next one is 09:21.
        Assert.Equal(At(9, 21), Engine.Status.NextCheckInUtc);
        await AdvanceToAsync(9, 21, 5);
        var shown = Assert.Single(Presenter.Shown);
        Assert.Equal(At(9, 21), shown.ScheduledUtc);
    }

    [Fact]
    public async Task ChangingIntervalWhilePopupIsOpen_DoesNotOpenASecondPopup()
    {
        await StartWithoutStartupPromptAsync();
        await AdvanceToAsync(9, 30, 5); // popup open

        await ConfigureAsync(t => t.CheckInIntervalMinutes = 1);
        await Engine.IdleAsync();
        await AdvanceToAsync(9, 33, 5);

        Assert.Single(Presenter.Shown);
        Assert.Single(await PromptsAsync());
    }

    [Fact]
    public async Task LongerInterval_MovesTheNextPromptOut()
    {
        await ConfigureAsync(t =>
        {
            t.PromptOnStart = false;
            t.CheckInIntervalMinutes = 1;
        });
        await StartEngineAsync();

        await ConfigureAsync(t => t.CheckInIntervalMinutes = 60);
        await Engine.IdleAsync();

        Assert.Equal(At(10, 0), Engine.Status.NextCheckInUtc);
        await AdvanceToAsync(9, 59);
        Assert.Empty(Presenter.Shown);
    }

    [Fact]
    public async Task TriggerNow_ShowsARealScheduledPrompt_AndKeepsTheSchedule()
    {
        await StartWithoutStartupPromptAsync();

        await Engine.TriggerCheckInNowAsync();
        Presenter.Complete(PromptOutcome.TimedOut);
        await Engine.IdleAsync();

        Assert.Equal(Start, Assert.Single(Presenter.Shown).ScheduledUtc);
        Assert.Equal(CheckInOutcome.Missed, Assert.Single(await PromptsAsync()).Outcome);
        Assert.Equal(At(9, 30), Engine.Status.NextCheckInUtc);
    }

    [Fact]
    public async Task TriggerNow_DoesNothingWhilePaused()
    {
        await StartWithoutStartupPromptAsync();
        await Engine.PauseAsync(null);

        await Engine.TriggerCheckInNowAsync();

        Assert.Empty(Presenter.Shown);
    }
}
