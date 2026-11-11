using System.Windows;
using Microsoft.EntityFrameworkCore;
using WorkTrace.App.Shell;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;
using WorkTrace.Infrastructure.Persistence;

namespace WorkTrace.App.Tests;

/// <summary>Mini Mode: the same application and data in a small window; switching never touches registrations.</summary>
public class MiniModeTests
{
    private static async Task<(int Segments, int Observations, int Allocations)> CountsAsync(UiTestContext ctx)
    {
        await using var db = await ctx.Get<IDbContextFactory<WorkTraceDbContext>>().CreateDbContextAsync();
        return (await db.TrackingSegments.CountAsync(), await db.Observations.CountAsync(), await db.ManualAllocations.CountAsync());
    }

    [Fact]
    public Task MiniMode_SwitchesWindows_ShowsLiveData_RemembersPlacement_AndNeverTouchesRegistrations() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var client = await ctx.Get<IClientService>().CreateAsync(new ClientInput("Contoso", null, false));
        var first = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Invoice posting error", null, null));
        var second = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, null, "Month-end close", null, null));
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, first.Id, null));

        var manager = ctx.Get<WindowManager>();
        var home = ctx.Get<HomeViewModel>();
        manager.ShowMainWindow();
        var main = Application.Current.Windows.OfType<MainWindow>().Single();
        main.Left = 40;
        main.Top = 50;
        main.Width = 1000;
        main.Height = 650;
        await WpfTestHost.SettleAsync();
        var before = await CountsAsync(ctx);
        WpfTestHost.BindingErrors.Take();
        WpfTestHost.UnhandledExceptions.Clear();
        try
        {
            home.ToggleMiniModeCommand.Execute(null); // Ctrl+M / Mini Mode button
            await WpfTestHost.SettleAsync();
            var mini = Assert.Single(Application.Current.Windows.OfType<MiniWindow>());
            Assert.True(manager.IsActive);
            Assert.False(main.IsVisible);
            Assert.True(mini.Topmost && mini.ShowInTaskbar);
            Assert.Same(home, mini.DataContext); // one view model, no competing state

            // Live: a task chosen meanwhile shows up in Mini Mode at once.
            await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, second.Id, null));
            await WpfTestHost.SettleAsync(12);
            Assert.Contains(home.DayTasks, t => t.TaskId == second.Id);

            mini.Topmost = false; // unpin
            mini.Left = 120;
            mini.Top = 80;
            await WpfTestHost.SettleAsync();

            manager.Toggle(); // expand / Ctrl+M / Esc
            await WpfTestHost.SettleAsync();
            Assert.False(manager.IsActive);
            Assert.Empty(Application.Current.Windows.OfType<MiniWindow>());
            Assert.True(main.IsVisible);
            Assert.Equal((40d, 50d, 1000d, 650d), (main.Left, main.Top, main.Width, main.Height)); // normal window as it was

            await manager.PlacementSaved;
            var saved = (await ctx.Get<ISettingsStore>().LoadAsync()).General.MiniWindow;
            Assert.Equal((false, 120d, 80d), (saved.Topmost, saved.Left, saved.Top)); // remembered for the next session

            // Switching modes never starts, stops or changes registrations (only the one check-in above was added).
            Assert.Equal(before with { Observations = before.Observations + 1 }, await CountsAsync(ctx));
        }
        finally
        {
            manager.CloseAll();
        }

        Assert.Empty(WpfTestHost.UnhandledExceptions);
        var errors = WpfTestHost.BindingErrors.Take();
        Assert.True(errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors));
    });
}
