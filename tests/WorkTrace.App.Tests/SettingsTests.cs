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

        vm.IntervalChoice = 5;
        vm.IsFocusedMode = true;
        vm.AutoDismissSeconds = 300;
        await WpfTestHost.SettleAsync();

        var saved = ctx.Get<ISettingsStore>().Current;
        Assert.Equal(5, saved.Tracking.CheckInIntervalMinutes);
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

    [Fact]
    public Task Interval_PresetsIncludeOneMinute_AndCustomValuesAreValidated() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<SettingsViewModel>();
        await vm.ActivateAsync(null);
        var store = ctx.Get<ISettingsStore>();

        Assert.Equal(30, vm.IntervalChoice); // default
        Assert.Equal([1, 5, 10, 15, 30, 60, SettingsViewModel.CustomIntervalKey], vm.IntervalOptions.Select(o => o.Id));

        vm.IntervalChoice = 1;
        await WpfTestHost.SettleAsync();
        Assert.Equal(1, store.Current.Tracking.CheckInIntervalMinutes);

        vm.IntervalChoice = SettingsViewModel.CustomIntervalKey;
        Assert.True(vm.IsCustomInterval);
        vm.CustomIntervalText = "0";
        await WpfTestHost.SettleAsync();
        Assert.NotNull(vm.IntervalError);
        Assert.Equal(1, store.Current.Tracking.CheckInIntervalMinutes);

        vm.CustomIntervalText = "37";
        await WpfTestHost.SettleAsync();
        Assert.Null(vm.IntervalError);
        Assert.Equal(37, store.Current.Tracking.CheckInIntervalMinutes);

        // Reopening the page shows a non-preset value as Custom.
        await vm.ActivateAsync(null);
        Assert.True(vm.IsCustomInterval);
        Assert.Equal("37", vm.CustomIntervalText);
    });
}
