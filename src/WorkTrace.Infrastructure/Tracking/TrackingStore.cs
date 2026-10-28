using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Tracking;

internal sealed class TrackingStore(IDbContextFactory<WorkTraceDbContext> contextFactory, ILogger<TrackingStore> logger) : ITrackingStore
{
    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var open = await db.TrackingSegments.Where(s => s.EndUtc == null).ToListAsync(cancellationToken);
        foreach (var segment in open)
        {
            // The last heartbeat is the latest moment we know the app was running.
            segment.EndUtc = segment.LastHeartbeatUtc < segment.StartUtc ? segment.StartUtc : segment.LastHeartbeatUtc;
            segment.EndReason = TrackingEndReason.Crash;
        }

        var pending = await db.CheckInPrompts.Where(p => p.Outcome == CheckInOutcome.Pending).ToListAsync(cancellationToken);
        foreach (var prompt in pending)
        {
            prompt.Outcome = CheckInOutcome.Missed;
            prompt.ResolvedAtUtc = prompt.ShownAtUtc ?? prompt.ScheduledAtUtc;
        }

        var openLocks = await db.LockPeriods.Where(l => l.EndUtc == null).ToListAsync(cancellationToken);
        foreach (var lockPeriod in openLocks)
        {
            // Unknown end: close it where tracking ended (it cannot have outlived the app).
            var end = open.Where(s => s.EndUtc >= lockPeriod.StartUtc).Select(s => s.EndUtc).Max();
            lockPeriod.EndUtc = end is { } e && e >= lockPeriod.StartUtc ? e : lockPeriod.StartUtc;
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        if (open.Count + pending.Count + openLocks.Count > 0)
        {
            logger.LogWarning(
                "Recovered from unclean exit: {Segments} segment(s), {Prompts} prompt(s), {Locks} lock period(s)",
                open.Count, pending.Count, openLocks.Count);
        }

        return open.Count;
    }

    public async Task<int> OpenSegmentAsync(DateTime startUtc, CancellationToken cancellationToken = default)
    {
        startUtc = UtcTime.Ensure(startUtc);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var segment = new TrackingSegment { StartUtc = startUtc, LastHeartbeatUtc = startUtc };
        db.TrackingSegments.Add(segment);
        await db.SaveChangesAsync(cancellationToken);
        return segment.Id;
    }

    public async Task CloseSegmentAsync(int segmentId, DateTime endUtc, TrackingEndReason reason, CancellationToken cancellationToken = default)
    {
        endUtc = UtcTime.Ensure(endUtc);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var segment = await db.TrackingSegments.SingleOrDefaultAsync(s => s.Id == segmentId, cancellationToken);
        if (segment is null || segment.EndUtc is not null)
        {
            return;
        }

        segment.EndUtc = endUtc < segment.StartUtc ? segment.StartUtc : endUtc;
        segment.LastHeartbeatUtc = segment.EndUtc.Value;
        segment.EndReason = reason;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task HeartbeatAsync(int segmentId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        atUtc = UtcTime.Ensure(atUtc);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.TrackingSegments
            .Where(s => s.Id == segmentId && s.EndUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastHeartbeatUtc, atUtc), cancellationToken);
    }

    public async Task<int> StartLockAsync(DateTime startUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var period = new LockPeriod { StartUtc = UtcTime.Ensure(startUtc) };
        db.LockPeriods.Add(period);
        await db.SaveChangesAsync(cancellationToken);
        return period.Id;
    }

    public async Task EndLockAsync(int lockId, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        endUtc = UtcTime.Ensure(endUtc);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var period = await db.LockPeriods.SingleOrDefaultAsync(l => l.Id == lockId, cancellationToken);
        if (period is null || period.EndUtc is not null)
        {
            return;
        }

        period.EndUtc = endUtc < period.StartUtc ? period.StartUtc : endUtc;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> CreatePromptAsync(DateTime scheduledAtUtc, DateTime? shownAtUtc, CheckInOutcome outcome, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var prompt = new CheckInPrompt
        {
            ScheduledAtUtc = UtcTime.Ensure(scheduledAtUtc),
            ShownAtUtc = UtcTime.Ensure(shownAtUtc),
            Outcome = outcome,
            ResolvedAtUtc = outcome == CheckInOutcome.Pending ? null : UtcTime.Ensure(shownAtUtc ?? scheduledAtUtc),
        };
        db.CheckInPrompts.Add(prompt);
        await db.SaveChangesAsync(cancellationToken);
        return prompt.Id;
    }

    public async Task ResolvePromptAsync(int promptId, CheckInOutcome outcome, DateTime resolvedAtUtc, CancellationToken cancellationToken = default)
    {
        resolvedAtUtc = UtcTime.Ensure(resolvedAtUtc);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.CheckInPrompts
            .Where(p => p.Id == promptId && p.Outcome == CheckInOutcome.Pending)
            .ExecuteUpdateAsync(
                s => s.SetProperty(p => p.Outcome, outcome).SetProperty(p => p.ResolvedAtUtc, resolvedAtUtc),
                cancellationToken);
    }
}
