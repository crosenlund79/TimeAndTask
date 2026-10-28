using WorkTrace.App.ViewModels;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Tests;

public class SettingsTests
{
    [Fact]
    public Task Changes_AreSavedImmediately() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<SettingsViewModel>();
        await vm.ActivateAsync(null);

        vm.IntervalMinutes = 45;
        vm.IsFocusedMode = true;
        vm.AutoDismissSeconds = 300;
        await WpfTestHost.SettleAsync();

        var saved = ctx.Get<ISettingsStore>().Current;
        Assert.Equal(45, saved.Tracking.CheckInIntervalMinutes);
        Assert.True(saved.Tracking.MaxEstimationHorizonMinutes >= 45);
        Assert.Equal(PopupMode.Focused, saved.Tracking.PopupMode);
        Assert.Equal(300, saved.Tracking.AutoDismissSeconds);
    });

    [Fact]
    public Task InvalidShortcut_IsRejected() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<SettingsViewModel>();
        await vm.ActivateAsync(null);
        var before = ctx.Get<ISettingsStore>().Current.General.GlobalShortcut;

        vm.Shortcut = "Space"; // no modifier
        await WpfTestHost.SettleAsync();

        Assert.NotNull(vm.ShortcutError);
        Assert.Equal(before, ctx.Get<ISettingsStore>().Current.General.GlobalShortcut);

        vm.Shortcut = "Ctrl+Shift+K";
        await WpfTestHost.SettleAsync();
        Assert.Null(vm.ShortcutError);
        Assert.Equal("Ctrl+Shift+K", ctx.Get<ISettingsStore>().Current.General.GlobalShortcut);
    });
}
