using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Board;
using WorkTrace.Core.Management;
using WorkTrace.Core.Reporting;
using WorkTrace.Infrastructure.Preview;

namespace WorkTrace.Tests.Management;

public class PreviewSampleDataTests : ManagementTestBase
{
    [Fact]
    public async Task SampleData_FillsAnEmptyDatabase_AndLeavesAnExistingOneAlone()
    {
        var seeder = Services.GetRequiredService<PreviewSampleData>();
        await seeder.SeedAsync(Ct);

        var all = await Tasks.SearchAsync(new TaskQuery(IncludeArchived: true), Ct);
        Assert.Equal(8, all.Count);
        Assert.Equal([TaskGroup.Regular, TaskGroup.Recurring, TaskGroup.Waiting, TaskGroup.Closed], all.Select(t => t.Group).Distinct().Order());
        Assert.NotEmpty(await Services.GetRequiredService<IBoardService>().GetCardsAsync(new CardFilter(), Ct));
        var lastWeek = await Services.GetRequiredService<ITimesheetService>().GetWeekAsync(DateOnly.FromDateTime(Time.GetUtcNow().UtcDateTime).AddDays(-7), Ct);
        Assert.True(lastWeek.WeekTotal > 20 * 60);

        await seeder.SeedAsync(Ct); // second start: nothing added
        Assert.Equal(8, (await Tasks.SearchAsync(new TaskQuery(IncludeArchived: true), Ct)).Count);
    }
}
