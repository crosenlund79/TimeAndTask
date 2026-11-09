using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Reporting;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Validation;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Reporting;

internal sealed class RegistrationService(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    ISettingsStore settings,
    TimeProvider timeProvider,
    ILogger<RegistrationService> logger) : IRegistrationService
{
    private static readonly TimeSpan MaxLength = TimeSpan.FromHours(24);

    public async Task AssignAsync(DateTime startUtc, DateTime endUtc, int? taskId, int? clientId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateAsync(db, startUtc, endUtc, taskId, clientId, cancellationToken);
        db.ManualAllocations.Add(Allocation(startUtc, endUtc, taskId, clientId));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Time registered ({Minutes} min)", (int)(endUtc - startUtc).TotalMinutes);
    }

    public async Task EditAsync(DateTime originalStartUtc, DateTime originalEndUtc, DateTime startUtc, DateTime endUtc, int? taskId, int? clientId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        CheckRange(originalStartUtc, originalEndUtc, "Original");
        await ValidateAsync(db, startUtc, endUtc, taskId, clientId, cancellationToken);

        // Clear the old range first, then register the new one: created later, so it wins where they overlap.
        var cleared = Cleared(originalStartUtc, originalEndUtc);
        db.ManualAllocations.Add(cleared);
        await db.SaveChangesAsync(cancellationToken);
        var registered = Allocation(startUtc, endUtc, taskId, clientId);
        registered.CreatedAtUtc = registered.ModifiedAtUtc = cleared.CreatedAtUtc.AddTicks(1);
        db.ManualAllocations.Add(registered);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Registration edited");
    }

    public async Task DeleteAsync(DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        CheckRange(startUtc, endUtc, "Start");
        db.ManualAllocations.Add(Cleared(startUtc, endUtc));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Registration deleted (now unclassified)");
    }

    public async Task<OpenInterval?> GetOpenIntervalAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var segment = await db.TrackingSegments.AsNoTracking().Where(s => s.EndUtc == null && s.StartUtc < nowUtc)
            .OrderByDescending(s => s.StartUtc).FirstOrDefaultAsync(cancellationToken);
        if (segment is null)
        {
            return null; // not tracking (paused, asleep, closed): nothing is outstanding
        }

        var from = segment.StartUtc > nowUtc.AddDays(-1) ? segment.StartUtc : nowUtc.AddDays(-1);
        var (periods, _, _, _, _) = await TimesheetService.EstimateAsync(db, from, nowUtc, nowUtc, settings.Current, cancellationToken);
        return periods.Count > 0 && periods[^1] is { Kind: PeriodKind.Unclassified } last && last.EndUtc >= nowUtc.AddSeconds(-1)
            && last.Duration >= TimeSpan.FromMinutes(1)
                ? new OpenInterval(last.StartUtc, nowUtc)
                : null;
    }

    private ManualAllocation Allocation(DateTime startUtc, DateTime endUtc, int? taskId, int? clientId)
    {
        var now = UtcTime.Now(timeProvider);
        return new ManualAllocation
        {
            StartUtc = startUtc,
            EndUtc = endUtc,
            Kind = taskId is null ? AllocationKind.Stakeholder : AllocationKind.Task,
            TaskId = taskId,
            ClientId = taskId is null ? clientId : null,
            CreatedAtUtc = now,
            ModifiedAtUtc = now,
        };
    }

    private ManualAllocation Cleared(DateTime startUtc, DateTime endUtc)
    {
        var now = UtcTime.Now(timeProvider);
        return new ManualAllocation { StartUtc = startUtc, EndUtc = endUtc, Kind = AllocationKind.Cleared, CreatedAtUtc = now, ModifiedAtUtc = now };
    }

    private static void CheckRange(DateTime startUtc, DateTime endUtc, string field)
    {
        UtcTime.Ensure(startUtc, nameof(startUtc));
        UtcTime.Ensure(endUtc, nameof(endUtc));
        if (endUtc <= startUtc)
        {
            throw DomainValidationException.ForField(field, "The end must be after the start.");
        }

        if (endUtc - startUtc > MaxLength)
        {
            throw DomainValidationException.ForField(field, "A registration can be at most 24 hours.");
        }
    }

    private static async Task ValidateAsync(WorkTraceDbContext db, DateTime startUtc, DateTime endUtc, int? taskId, int? clientId, CancellationToken cancellationToken)
    {
        CheckRange(startUtc, endUtc, "Start");
        if (taskId is { } t && !await db.Tasks.AnyAsync(x => x.Id == t, cancellationToken))
        {
            throw DomainValidationException.ForField("Task", "Choose an existing task.");
        }

        if (taskId is null)
        {
            if (clientId is not { } c)
            {
                throw DomainValidationException.ForField("Task", "Choose a task or a stakeholder.");
            }

            if (!await db.Clients.AnyAsync(x => x.Id == c, cancellationToken))
            {
                throw DomainValidationException.ForField("Stakeholder", "Choose an existing stakeholder.");
            }
        }
    }
}
