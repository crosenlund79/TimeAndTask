using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Tracking;
using WorkTrace.Core.Validation;

namespace WorkTrace.Tests.Tracking;

public class CheckInServiceTests : TrackingTestBase
{
    private ITaskService Tasks => Services.GetRequiredService<ITaskService>();

    private async Task<TaskSummary> CreateTaskAsync(string title = "Invoice posting")
    {
        var client = (await Services.GetRequiredService<IClientService>().GetClientsAsync(cancellationToken: Ct))[0];
        return await Tasks.CreateAsync(new TaskInput(client.Id, null, "BUG-1", title, null, null), Ct);
    }

    [Fact]
    public async Task ManualCheckIn_StoresObservation_AndMarksTaskUsed()
    {
        var task = await CreateTaskAsync();

        var observation = await CheckIns.RecordAsync(new CheckInRequest(null, task.Id, "  call with Anna "), Ct);

        Assert.Equal(ObservationOrigin.Manual, observation.Origin);
        Assert.Equal(Start, observation.ObservedAtUtc);
        Assert.Equal("call with Anna", observation.Note);
        Assert.Equal("Invoice posting", observation.TaskTitle);
        Assert.Equal("Internal", observation.ClientName);
        Assert.False(observation.IsCorrected);
        Assert.Equal(Start, (await Tasks.GetAsync(task.Id, Ct)).LastUsedAtUtc);
    }

    [Fact]
    public async Task AnsweringAPrompt_MarksItAnswered_EvenAfterItTimedOut()
    {
        var task = await CreateTaskAsync();
        var promptId = await Store.CreatePromptAsync(Start, Start, CheckInOutcome.Pending, Ct);
        await Store.ResolvePromptAsync(promptId, CheckInOutcome.Missed, Start.AddMinutes(2), Ct);

        var observation = await CheckIns.RecordAsync(new CheckInRequest(promptId, task.Id, null), Ct);

        var prompt = Assert.Single(await PromptsAsync());
        Assert.Equal(CheckInOutcome.Answered, prompt.Outcome);
        Assert.Equal(observation.Id, prompt.ObservationId);
        Assert.Equal(ObservationOrigin.Scheduled, observation.Origin);
    }

    [Fact]
    public async Task CheckIn_NeedsTaskOrNote()
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(() => CheckIns.RecordAsync(new CheckInRequest(null, null, "  "), Ct));
        Assert.NotNull(ex.Errors.For(nameof(CheckInRequest.TaskId)));

        var noteOnly = await CheckIns.RecordAsync(new CheckInRequest(null, null, "Unplanned firefighting"), Ct);
        Assert.Null(noteOnly.TaskId);
    }

    [Fact]
    public async Task ObservedTime_CanBeEarlier_ButNotInTheFuture()
    {
        var task = await CreateTaskAsync();

        var earlier = await CheckIns.RecordAsync(new CheckInRequest(null, task.Id, null, ObservedAtUtc: Start.AddMinutes(-40)), Ct);
        Assert.Equal(Start.AddMinutes(-40), earlier.ObservedAtUtc);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(() =>
            CheckIns.RecordAsync(new CheckInRequest(null, task.Id, null, ObservedAtUtc: Start.AddMinutes(10)), Ct));
        Assert.NotNull(ex.Errors.For(nameof(CheckInRequest.ObservedAtUtc)));
    }

    [Fact]
    public async Task UnknownTask_IsRejected_AndNothingIsStored()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => CheckIns.RecordAsync(new CheckInRequest(null, 999, "x"), Ct));
        Assert.Null(await CheckIns.GetLastObservationAsync(Ct));
    }

    [Fact]
    public async Task LastObservation_IsByObservedTime()
    {
        var a = await CreateTaskAsync("A");
        var b = await CreateTaskAsync("B");
        await CheckIns.RecordAsync(new CheckInRequest(null, a.Id, null), Ct);
        await CheckIns.RecordAsync(new CheckInRequest(null, b.Id, null, ObservedAtUtc: Start.AddHours(-1)), Ct);

        Assert.Equal("A", (await CheckIns.GetLastObservationAsync(Ct))!.TaskTitle);
    }

    [Fact]
    public async Task DayTimeline_ListsFactsInOrder_ForThatLocalDayOnly()
    {
        var task = await CreateTaskAsync();
        var segment = await Store.OpenSegmentAsync(Start, Ct);
        await CheckIns.RecordAsync(new CheckInRequest(null, task.Id, "deep work"), Ct);
        var missed = await Store.CreatePromptAsync(Start.AddMinutes(23), Start.AddMinutes(23), CheckInOutcome.Pending, Ct);
        await Store.ResolvePromptAsync(missed, CheckInOutcome.Missed, Start.AddMinutes(25), Ct);
        var lockId = await Store.StartLockAsync(Start.AddMinutes(40), Ct);
        await Store.EndLockAsync(lockId, Start.AddMinutes(50), Ct);
        await Store.CloseSegmentAsync(segment, Start.AddHours(1), TrackingEndReason.Paused, Ct);
        await CheckIns.RecordAsync(new CheckInRequest(null, task.Id, null, ObservedAtUtc: Start.AddDays(-1)), Ct);

        var timeline = await CheckIns.GetDayTimelineAsync(DateOnly.FromDateTime(Start), TimeZoneInfo.Utc, Ct);

        Assert.Equal(
            [
                TimelineEventKind.TrackingStarted, TimelineEventKind.CheckIn, TimelineEventKind.MissedCheckIn,
                TimelineEventKind.Locked, TimelineEventKind.Unlocked, TimelineEventKind.TrackingStopped,
            ],
            timeline.Select(e => e.Kind));
        Assert.Equal("Invoice posting", timeline[1].Title);
        Assert.Contains("deep work", timeline[1].Detail, StringComparison.Ordinal);
        Assert.Equal("Tracking paused", timeline[^1].Title);
    }

    [Fact]
    public async Task Recovery_ClosesOpenLockAtCrashedSegmentEnd()
    {
        var segment = await Store.OpenSegmentAsync(Start, Ct);
        await Store.HeartbeatAsync(segment, Start.AddMinutes(30), Ct);
        await Store.StartLockAsync(Start.AddMinutes(20), Ct);

        Assert.Equal(1, await Store.RecoverAsync(Ct));

        var lockPeriod = Assert.Single(await LocksAsync());
        Assert.Equal(Start.AddMinutes(30), lockPeriod.EndUtc);
        Assert.Equal(0, await Store.RecoverAsync(Ct)); // idempotent
    }
}
