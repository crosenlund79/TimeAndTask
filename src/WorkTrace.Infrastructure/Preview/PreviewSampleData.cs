using Microsoft.EntityFrameworkCore;
using WorkTrace.Core.Board;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Guides;
using WorkTrace.Core.Management;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.Infrastructure.Preview;

/// <summary>
/// Fictional sample data for visual-preview builds (their own, empty database only): stakeholders, tasks in every
/// group, Board cards, two weeks of registrations and a few guides, so every page has something to show.
/// </summary>
public sealed class PreviewSampleData(
    IDbContextFactory<WorkTraceDbContext> contextFactory,
    IClientService clients,
    ITaskService tasks,
    IBoardService board,
    IGuideService guides,
    TimeProvider timeProvider)
{
    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            if (await db.Tasks.AnyAsync(cancellationToken))
            {
                return; // never touch a database that already has data
            }
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcTime.Now(timeProvider), TimeZoneInfo.Local));
        var northwind = (await clients.CreateAsync(new ClientInput("Northwind Logistics", "Route planning and carrier integrations", false, "#5B7DB1"), cancellationToken)).Id;
        var contoso = (await clients.CreateAsync(new ClientInput("Contoso Finance", "Finance reporting", false, "#6B8E5A"), cancellationToken)).Id;
        var fabrikam = (await clients.CreateAsync(new ClientInput("Fabrikam Health", "Patient portal", false, "#A0675E"), cancellationToken)).Id;
        var internalId = (await clients.GetClientsAsync(cancellationToken: cancellationToken)).First(c => c.IsInternal).Id;

        async Task<int> Task(int client, string? reference, string title, int? estimate = null, DateOnly? deadline = null, DateOnly? planned = null,
            bool recurring = false, string? waiting = null, bool closed = false) =>
            (await tasks.CreateAsync(
                new TaskInput(client, null, reference, title, null, null, Deadline: deadline, IsRecurring: recurring, IsWaiting: waiting is not null,
                    WaitingReason: waiting, IsClosed: closed, EstimatedRemainingMinutes: estimate, PlannedStart: planned),
                cancellationToken)).Id;

        var route = await Task(northwind, "NW-142", "Route optimisation dashboard", 360, deadline: today.AddDays(2));
        var carrier = await Task(northwind, "NW-151", "Carrier API integration", 480, waiting: "API keys and sandbox access from Northwind IT");
        var quarter = await Task(contoso, "CF-88", "Quarter-end reconciliation report", 180, planned: today.AddDays(1));
        var sync = await Task(contoso, null, "Weekly finance sync", 60, recurring: true);
        var portal = await Task(fabrikam, "FH-23", "Patient portal accessibility review");
        await Task(fabrikam, "FH-19", "Release notes v2.4", closed: true);
        var standup = await Task(internalId, null, "Team stand-up", 15, recurring: true);
        var admin = await Task(internalId, null, "Timesheet and admin", 30);

        await board.CreateAsync(new CardFields("Evaluate the visual refresh", BoardCategory.Feature, BoardPriority.High,
            "Compare the five preview skins and pick a direction.", "One theme chosen", null, null), BoardStatus.Backlog, cancellationToken);
        await board.CreateAsync(new CardFields("Excel export polish", BoardCategory.Feature, BoardPriority.Normal, null, null, null, null), BoardStatus.Doing, cancellationToken);
        await board.CreateAsync(new CardFields("Dark mode contrast check", BoardCategory.Bug, BoardPriority.Normal, null, null, null, null), BoardStatus.Review, cancellationToken);
        await board.CreateAsync(new CardFields("Archive old reports", null, BoardPriority.Low, null, null, null, null), BoardStatus.Todo, cancellationToken);

        var weekdayTasks = new[] { route, quarter, portal, carrier, route };
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // Two weeks of working days (and today's morning) with switches between tasks.
            for (var back = 13; back >= 0; back--)
            {
                var day = today.AddDays(-back);
                if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    continue;
                }

                DateTime At(int hour, int minute) =>
                    TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute)), TimeZoneInfo.Local);

                var main = weekdayTasks[day.DayNumber % weekdayTasks.Length];
                var slots = new (int H, int M, int? Task, int? Client)[]
                {
                    (8, 30, standup, null), (8, 45, main, null), (10, 15, admin, null), (10, 40, main, null),
                    (12, 0, null, null), (12, 40, quarter, null), (14, 10, null, fabrikam), (14, 50, main, null),
                };
                var end = back == 0 ? At(12, 0) : At(16, 30);
                if (end > UtcTime.Now(timeProvider))
                {
                    continue;
                }

                db.TrackingSegments.Add(new TrackingSegment { StartUtc = At(8, 25), EndUtc = At(12, 0), LastHeartbeatUtc = At(12, 0), EndReason = TrackingEndReason.Paused });
                if (back > 0)
                {
                    db.TrackingSegments.Add(new TrackingSegment { StartUtc = At(12, 40), EndUtc = end, LastHeartbeatUtc = end, EndReason = TrackingEndReason.Shutdown });
                }

                if (day.DayOfWeek == DayOfWeek.Monday)
                {
                    slots = [.. slots, (15, 30, sync, null)];
                }

                foreach (var (h, m, task, client) in slots)
                {
                    var at = At(h, m);
                    if (task is null && client is null || at >= end)
                    {
                        continue;
                    }

                    db.Observations.Add(Observation.Create(at, at, ObservationOrigin.Scheduled, task, clientId: client));
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        var g1 = await guides.CreateAsync("Submitting the weekly timesheet", "Admin", cancellationToken);
        await guides.UpdateAsync(g1.Id, g1.Title, "Admin",
            "1. Open Timesheet and check each day.\n2. Correct cells where needed.\n3. Export to Excel and upload it to the portal before Friday 15:00.", cancellationToken);
        var g2 = await guides.CreateAsync("Release checklist", "Engineering", cancellationToken);
        await guides.UpdateAsync(g2.Id, g2.Title, "Engineering",
            "- Update release notes\n- Run the full test suite\n- Tag the build\n- Announce in the team channel", cancellationToken);
    }
}
