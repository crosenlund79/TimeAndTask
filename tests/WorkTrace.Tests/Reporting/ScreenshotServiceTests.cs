using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Screenshots;
using WorkTrace.Tests.Management;

namespace WorkTrace.Tests.Reporting;

public class ScreenshotServiceTests : ManagementTestBase
{
    private IScreenshotService Shots => Services.GetRequiredService<IScreenshotService>();

    [Fact]
    public async Task Captures_AreStoredByDay_WithTheirCheckIn_AndCanBeDeletedOrExpire()
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var task = await Tasks.CreateAsync(new TaskInput(await InternalClientIdAsync(), null, null, "Invoice export", null, null), Ct);
        await AddObservationAsync(task.Id);
        var a = await Shots.SaveAsync([1, 2, 3], ".jpg", 100, 50, now.AddMinutes(5), Ct);
        var b = await Shots.SaveAsync([4, 5], ".jpg", 100, 50, now.AddDays(-30), Ct);

        var today = await Shots.GetDayAsync(DateOnly.FromDateTime(now.AddMinutes(5).ToLocalTime()), Ct);
        Assert.Equal("Invoice export", Assert.Single(today).Context);
        Assert.True(File.Exists(a.FullPath));
        Assert.Equal(DateOnly.FromDateTime(now.AddDays(-30).ToLocalTime()), await Shots.GetAdjacentDayAsync(DateOnly.FromDateTime(now.ToLocalTime()), false, Ct));

        Assert.Equal(1, await Shots.DeleteOlderThanAsync(now.AddDays(-14), Ct));
        Assert.False(File.Exists(b.FullPath));
        await Shots.DeleteAsync([a.Id], Ct);
        Assert.Equal((0, 0L), await Shots.GetStorageAsync(Ct));
        Assert.False(File.Exists(a.FullPath));
    }
}
