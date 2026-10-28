using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkTrace.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create migrations; never at runtime.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkTraceDbContext>
{
    public WorkTraceDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkTraceDbContext>()
            .UseSqlite(SqliteConnectionStrings.ForFile("design-time.db"))
            .Options;
        return new WorkTraceDbContext(options);
    }
}
