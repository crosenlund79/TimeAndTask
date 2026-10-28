using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Common;
using WorkTrace.Core.Entities;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>
/// Seeds default reference data. Each table is seeded only while it is empty, so renaming or
/// deactivating a seeded item is respected and seeding is idempotent.
/// </summary>
public sealed class DataSeeder(TimeProvider timeProvider, ILogger<DataSeeder> logger)
{
    public async Task SeedAsync(WorkTraceDbContext db, CancellationToken cancellationToken = default)
    {
        var now = UtcTime.Now(timeProvider);
        var seeded = new List<string>();

        if (!await db.Clients.AnyAsync(cancellationToken))
        {
            db.Clients.Add(new Client
            {
                Name = Client.DefaultInternalName,
                Description = "Internal, non-billable work.",
                IsInternal = true,
                IsActive = true,
                CreatedAtUtc = now,
                ModifiedAtUtc = now,
            });
            seeded.Add("Clients");
        }

        if (!await db.TaskSystems.AnyAsync(cancellationToken))
        {
            db.TaskSystems.AddRange(TaskSystem.BuiltInNames.Select((name, i) =>
                new TaskSystem { Name = name, SortOrder = i * 10, IsBuiltIn = true }));
            seeded.Add("TaskSystems");
        }

        if (!await db.CommunicationChannels.AnyAsync(cancellationToken))
        {
            db.CommunicationChannels.AddRange(CommunicationChannel.BuiltInNames.Select((name, i) =>
                new CommunicationChannel { Name = name, SortOrder = i * 10, IsBuiltIn = true }));
            seeded.Add("CommunicationChannels");
        }

        if (seeded.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded default data for {Tables}", string.Join(", ", seeded));
        }
    }
}
