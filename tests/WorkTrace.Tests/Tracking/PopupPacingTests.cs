using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Tracking;

namespace WorkTrace.Tests.Tracking;

/// <summary>Automatic check-ins wait for a pause in keyboard/mouse use and are never closer than the check-in interval.</summary>
public class PopupPacingTests : TrackingTestBase
{
    private static readonly TimeSpan Typing = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Away = TimeSpan.FromSeconds(30);

    private Task Configure() => ConfigureAsync(t =>
    {
        t.PromptOnStart = false;
        t.CheckInIntervalMinutes = 10;
        t.PopupIdleSeconds = 10;
        t.PopupRetrySeconds = 60;
    });

    [Fact]
    public async Task ADueCheckIn_WaitsWhileTheUserIsActive_ThenAppearsOnce_AndTheNextIsAFullIntervalLater()
    {
        await Configure();
        var client = (await Services.GetRequiredService<IClientService>().GetClientsAsync(cancellationToken: Ct))[0].Id;
        var task = await Services.GetRequiredService<ITaskService>().CreateAsync(new TaskInput(client, null, null, "Task A", null, null), Ct);
        await StartEngineAsync();
        await CheckIns.RecordAsync(new CheckInRequest(null, task.Id, null), Ct);
        Idle.IdleTime = Typing;

        await AdvanceToAsync(9, 12, 30); // the 09:10 slot and two retries while typing
        Assert.Empty(Presenter.Shown);
        Assert.Empty(await PromptsAsync()); // nothing stored while deferred
        Assert.Equal(CheckInAttemptResult.DeferredActive, Engine.Status.LastAttempt!.Result);

        Idle.IdleTime = Away;
        await AdvanceToAsync(9, 13, 30);
        var shown = Assert.Single(Presenter.Shown);
        Assert.Equal(At(9, 10), shown.ScheduledUtc); // the owed 09:10 check-in, not one per missed retry
        var appeared = (await PromptsAsync()).Single().ShownAtUtc!.Value;
        Presenter.Complete(PromptOutcome.TimedOut);
        await Engine.IdleAsync();

        Assert.Equal(appeared.AddMinutes(10), Engine.Status.NextCheckInUtc); // a full interval after it appeared
        await AdvanceAsync(appeared.AddMinutes(10) - Now - TimeSpan.FromSeconds(1));
        Assert.Single(Presenter.Shown);

        // Deferral never touches the registration: Task A ran on throughout.
        var active = await Services.GetRequiredService<IRegistrationService>().GetActiveAsync(Now, Ct);
        Assert.Equal(task.Id, active!.TaskId);
    }

    [Fact]
    public async Task StartAndWelcomeBackPrompts_NeverComeWithinAnIntervalOfTheLastAutomaticPopup()
    {
        await ConfigureAsync(t => { t.PromptOnStart = true; t.CheckInIntervalMinutes = 10; });
        await StartEngineAsync();                      // 09:07 start; start-up prompt at 09:07:20
        await AdvanceToAsync(9, 7, 30);
        Assert.Single(Presenter.Shown);
        Presenter.Complete(PromptOutcome.Dismissed);   // closing it does not bring a replacement
        await Engine.IdleAsync();

        await Engine.PauseAsync(TimeSpan.FromMinutes(2)); // resumes 09:09:30 with a "welcome back" prompt …
        await AdvanceToAsync(9, 12);
        Assert.Single(Presenter.Shown);                // … which is too soon after 09:07:20
        Assert.Equal(CheckInAttemptResult.SkippedTooSoon, Engine.Status.LastAttempt!.Result);

        await AdvanceToAsync(9, 17, 30);
        var times = (await PromptsAsync()).Where(p => p.ShownAtUtc is not null).Select(p => p.ShownAtUtc!.Value).Order().ToList();
        Assert.Equal(2, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMinutes(10), $"{times[0]:HH:mm:ss} and {times[1]:HH:mm:ss}");
    }

    [Fact]
    public async Task AManualCheckIn_AppearsAtOnceWhileTyping_AndLeavesTheAutomaticScheduleAlone()
    {
        await Configure();
        await StartEngineAsync();
        Idle.IdleTime = Typing;
        var next = Engine.Status.NextCheckInUtc;

        Assert.Equal(CheckInAttemptResult.Shown, await Engine.TriggerCheckInNowAsync());
        Assert.Single(Presenter.Shown);
        Assert.Equal(next, Engine.Status.NextCheckInUtc);

        Assert.Equal(CheckInAttemptResult.SkippedPopupOpen, await Engine.TriggerCheckInNowAsync()); // only one popup at a time
        Assert.Single(Presenter.Shown);
    }
}
