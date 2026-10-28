using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Persistence;
using WorkTrace.Infrastructure.Tracking;

namespace WorkTrace.Tests.Tracking;

/// <summary>Real SQLite + settings file, fake clock, fake popup.</summary>
public abstract class TrackingTestBase : IAsyncLifetime
{
    /// <summary>Wednesday 28 Oct 2026, 09:07 UTC (tests use the UTC zone, so slots are :00/:30 UTC).</summary>
    protected static readonly DateTime Start = new(2026, 10, 28, 9, 7, 0, DateTimeKind.Utc);

    private readonly TestDataDirectory _data = new();

    protected ServiceProvider Services { get; private set; } = null!;

    protected FakeTimeProvider Clock => _data.Time;

    /// <summary>Elapsed-time source; normally advanced together with <see cref="Clock"/>.</summary>
    protected FakeTimeProvider Monotonic { get; } = new(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    protected FakePresenter Presenter { get; } = new();

    protected ISettingsStore Settings => Services.GetRequiredService<ISettingsStore>();

    protected ITrackingStore Store => Services.GetRequiredService<ITrackingStore>();

    protected ICheckInService CheckIns => Services.GetRequiredService<ICheckInService>();

    protected TrackingEngine Engine { get; private set; } = null!;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected DateTime Now => Clock.GetUtcNow().UtcDateTime;

    public async ValueTask InitializeAsync()
    {
        Clock.SetUtcNow(Start);
        Services = _data.BuildServices();
        await Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(Ct);
        await Settings.LoadAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Engine is not null)
        {
            await Engine.DisposeAsync();
        }

        await Services.DisposeAsync();
        _data.Dispose();
        GC.SuppressFinalize(this);
    }

    protected async Task ConfigureAsync(Action<TrackingSettings> change)
    {
        var copy = Settings.Current.Clone();
        change(copy.Tracking);
        await Settings.SaveAsync(copy, Ct);
    }

    protected async Task StartEngineAsync()
    {
        Engine = new TrackingEngine(Store, Settings, Presenter, Clock, NullLogger<TrackingEngine>.Instance, TimeZoneInfo.Utc, Monotonic);
        await Engine.StartAsync(Ct);
    }

    protected async Task RestartEngineAsync()
    {
        await Engine.DisposeAsync();
        await StartEngineAsync();
    }

    /// <summary>Moves real time forward (wall clock and elapsed time) and lets the engine catch up.</summary>
    /// <summary>
    /// The computer sleeps: both clocks move on but no timer fires (nothing runs while asleep). Timers that
    /// came due fire on the next <see cref="AdvanceAsync"/>, as overdue timers do after waking.
    /// </summary>
    protected void Sleep(TimeSpan duration)
    {
        Monotonic.AdjustTime(Monotonic.GetUtcNow() + duration);
        Clock.AdjustTime(Clock.GetUtcNow() + duration);
    }

    protected async Task AdvanceAsync(TimeSpan by)
    {
        // Step in small increments so timers fire at their due times, as they would in reality.
        var step = TimeSpan.FromSeconds(10);
        for (var elapsed = TimeSpan.Zero; elapsed < by; elapsed += step)
        {
            var delta = by - elapsed < step ? by - elapsed : step;
            Monotonic.Advance(delta);
            Clock.Advance(delta);
            await Engine.IdleAsync();
        }
    }

    protected async Task AdvanceToAsync(int hour, int minute, int second = 0)
    {
        var target = new DateTime(Start.Year, Start.Month, Start.Day, hour, minute, second, DateTimeKind.Utc);
        await AdvanceAsync(target - Now);
    }

    protected async Task<List<TrackingSegment>> SegmentsAsync()
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        return await db.TrackingSegments.OrderBy(s => s.Id).ToListAsync(Ct);
    }

    protected async Task<List<CheckInPrompt>> PromptsAsync()
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        return await db.CheckInPrompts.OrderBy(p => p.Id).ToListAsync(Ct);
    }

    protected async Task<List<LockPeriod>> LocksAsync()
    {
        await using var db = await Services.GetRequiredService<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync(Ct);
        return await db.LockPeriods.OrderBy(l => l.Id).ToListAsync(Ct);
    }

    protected static DateTime At(int hour, int minute, int second = 0) =>
        new(Start.Year, Start.Month, Start.Day, hour, minute, second, DateTimeKind.Utc);
}

/// <summary>Records popup requests; each stays open until the test completes it.</summary>
public sealed class FakePresenter : ICheckInPresenter
{
    private readonly List<(int PromptId, DateTime ScheduledUtc, PromptReason Reason, TaskCompletionSource<PromptOutcome> Completion)> _shown = [];

    public bool IsOpen => _shown.Any(s => !s.Completion.Task.IsCompleted);

    public IReadOnlyList<(int PromptId, DateTime ScheduledUtc, PromptReason Reason)> Shown =>
        [.. _shown.Select(s => (s.PromptId, s.ScheduledUtc, s.Reason))];

    public Task<PromptOutcome> ShowPromptAsync(int promptId, DateTime scheduledAtUtc, PromptReason reason, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<PromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetResult(PromptOutcome.Closed));
        _shown.Add((promptId, scheduledAtUtc, reason, completion));
        return completion.Task;
    }

    public void Complete(PromptOutcome outcome) => _shown.Last(s => !s.Completion.Task.IsCompleted).Completion.SetResult(outcome);

    public void CloseAll()
    {
        foreach (var shown in _shown)
        {
            shown.Completion.TrySetResult(PromptOutcome.Closed);
        }
    }
}
