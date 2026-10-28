using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Tracking;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Tracking;

internal sealed class CheckInService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<CheckInService> logger) : ICheckInService
{
    /// <summary>Small allowance for clock skew between the popup and the service.</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(2);

    public async Task<ObservationSummary> RecordAsync(CheckInRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = UtcTime.Now(timeProvider);
        var note = Text.Clean(request.Note);
        var observedAt = request.ObservedAtUtc is { } at ? UtcTime.Ensure(at) : now;

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var errors = new ValidationErrors();
        if (request.TaskId is null && note is null)
        {
            errors.Add(nameof(CheckInRequest.TaskId), "Choose a task or write a short note.");
        }

        if (note?.Length > Observation.NoteMaxLength)
        {
            errors.Add(nameof(CheckInRequest.Note), $"Use at most {Observation.NoteMaxLength} characters.");
        }

        if (observedAt > now + FutureTolerance)
        {
            errors.Add(nameof(CheckInRequest.ObservedAtUtc), "The time can't be in the future.");
        }

        if (request.TaskId is { } taskId && !await db.Tasks.AnyAsync(t => t.Id == taskId, cancellationToken))
        {
            errors.Add(nameof(CheckInRequest.TaskId), "The selected task no longer exists.");
        }

        if (request.ChannelId is { } channelId && !await db.CommunicationChannels.AnyAsync(c => c.Id == channelId, cancellationToken))
        {
            errors.Add(nameof(CheckInRequest.ChannelId), "The selected channel no longer exists.");
        }

        errors.ThrowIfInvalid();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var observation = Observation.Create(
            observedAt,
            now,
            request.PromptId is null ? ObservationOrigin.Manual : ObservationOrigin.Scheduled,
            request.TaskId,
            request.ChannelId,
            note);
        db.Observations.Add(observation);
        await db.SaveChangesAsync(cancellationToken);

        if (request.PromptId is { } promptId)
        {
            // A late answer (after the popup timed out) still counts: the user did tell us.
            await db.CheckInPrompts
                .Where(p => p.Id == promptId && p.Outcome != CheckInOutcome.Answered)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.Outcome, CheckInOutcome.Answered)
                        .SetProperty(p => p.ObservationId, observation.Id)
                        .SetProperty(p => p.ResolvedAtUtc, now),
                    cancellationToken);
        }

        if (request.TaskId is { } usedTaskId)
        {
            await db.Tasks.Where(t => t.Id == usedTaskId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAtUtc, now), cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Observation {ObservationId} recorded ({Origin})", observation.Id, observation.Origin);

        return await Summaries(db.Observations.Where(o => o.Id == observation.Id)).SingleAsync(cancellationToken);
    }

    public async Task<ObservationSummary?> GetLastObservationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await Summaries(db.Observations.Where(o => !o.IsDeleted)
                .OrderByDescending(o => o.ObservedAtUtc).ThenByDescending(o => o.Id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TimelineEvent>> GetDayTimelineAsync(DateOnly localDate, TimeZoneInfo timeZone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var from = LocalMidnightUtc(localDate, timeZone);
        var to = LocalMidnightUtc(localDate.AddDays(1), timeZone);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var events = new List<TimelineEvent>();

        var observations = await Summaries(db.Observations.Where(o => !o.IsDeleted && o.ObservedAtUtc >= from && o.ObservedAtUtc < to))
            .ToListAsync(cancellationToken);
        events.AddRange(observations.Select(o => new TimelineEvent(
            o.ObservedAtUtc,
            TimelineEventKind.CheckIn,
            o.TaskTitle ?? o.Note ?? "Check-in",
            Join(o.ClientName, o.ExternalId, o.TaskTitle is null ? null : o.Note, o.Origin == ObservationOrigin.Manual ? "manual" : null))));

        var prompts = await db.CheckInPrompts
            .Where(p => p.ScheduledAtUtc >= from && p.ScheduledAtUtc < to
                && (p.Outcome == CheckInOutcome.Missed || p.Outcome == CheckInOutcome.Dismissed || p.Outcome == CheckInOutcome.Suppressed))
            .ToListAsync(cancellationToken);
        events.AddRange(prompts.Select(p => p.Outcome switch
        {
            CheckInOutcome.Missed => new TimelineEvent(p.ScheduledAtUtc, TimelineEventKind.MissedCheckIn, "Missed check-in", "Time after this is unclassified until the next check-in"),
            CheckInOutcome.Dismissed => new TimelineEvent(p.ScheduledAtUtc, TimelineEventKind.DismissedCheckIn, "Skipped check-in", "Time after this is unclassified until the next check-in"),
            _ => new TimelineEvent(p.ScheduledAtUtc, TimelineEventKind.SuppressedCheckIn, "Check-in not shown", "The session was locked"),
        }));

        var segments = await db.TrackingSegments
            .Where(s => (s.StartUtc >= from && s.StartUtc < to) || (s.EndUtc >= from && s.EndUtc < to))
            .ToListAsync(cancellationToken);
        foreach (var segment in segments)
        {
            if (segment.StartUtc >= from && segment.StartUtc < to)
            {
                events.Add(new TimelineEvent(segment.StartUtc, TimelineEventKind.TrackingStarted, "Tracking started", null));
            }

            if (segment.EndUtc is { } end && end >= from && end < to)
            {
                events.Add(new TimelineEvent(end, TimelineEventKind.TrackingStopped, StopTitle(segment.EndReason), null));
            }
        }

        var locks = await db.LockPeriods
            .Where(l => (l.StartUtc >= from && l.StartUtc < to) || (l.EndUtc >= from && l.EndUtc < to))
            .ToListAsync(cancellationToken);
        foreach (var period in locks)
        {
            if (period.StartUtc >= from && period.StartUtc < to)
            {
                events.Add(new TimelineEvent(period.StartUtc, TimelineEventKind.Locked, "Screen locked", "Locked time is uncertain"));
            }

            if (period.EndUtc is { } end && end >= from && end < to)
            {
                events.Add(new TimelineEvent(end, TimelineEventKind.Unlocked, "Screen unlocked", null));
            }
        }

        return [.. events.OrderBy(e => e.AtUtc).ThenBy(e => SameInstantRank(e.Kind))];
    }

    /// <summary>Natural reading order for events at the same instant: start, then activity, then stop.</summary>
    private static int SameInstantRank(TimelineEventKind kind) => kind switch
    {
        TimelineEventKind.TrackingStarted => 0,
        TimelineEventKind.Unlocked => 1,
        TimelineEventKind.TrackingStopped => 9,
        TimelineEventKind.Locked => 8,
        _ => 5,
    };

    private static string StopTitle(TrackingEndReason reason) => reason switch
    {
        TrackingEndReason.Paused => "Tracking paused",
        TrackingEndReason.Shutdown => "WorkTrace closed",
        TrackingEndReason.Sleep => "Computer went to sleep",
        TrackingEndReason.Crash => "WorkTrace stopped unexpectedly",
        TrackingEndReason.ClockChange => "System clock changed",
        _ => "Tracking stopped",
    };

    private static string? Join(params string?[] parts)
    {
        var present = parts.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return present.Count == 0 ? null : string.Join(" · ", present);
    }

    private static DateTime LocalMidnightUtc(DateOnly date, TimeZoneInfo timeZone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);

        // Midnight can fall into a DST gap in a few zones; move forward to the first valid minute.
        while (timeZone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, timeZone);
    }

    private static IQueryable<ObservationSummary> Summaries(IQueryable<Observation> observations) =>
        observations.Select(o => new ObservationSummary(
            o.Id,
            o.ObservedAtUtc,
            o.CreatedAtUtc,
            o.Origin,
            o.TaskId,
            o.Task != null ? o.Task.Title : null,
            o.Task != null ? o.Task.ExternalId : null,
            o.Task != null ? o.Task.ClientId : null,
            o.Task != null ? o.Task.Client!.Name : null,
            o.ChannelId,
            o.Channel != null ? o.Channel.Name : null,
            o.Note,
            o.IsDeleted
                || o.ObservedAtUtc != o.OriginalObservedAtUtc
                || o.TaskId != o.OriginalTaskId
                || o.Note != o.OriginalNote));
}
