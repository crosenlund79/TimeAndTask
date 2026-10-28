using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WorkTrace.App.Tracking;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tests;

/// <summary>
/// Keyboard focus of the check-in popup. Logical focus (the search box is the popup's focused element)
/// is always asserted. Whether Windows lets the test process activate windows depends on the desktop
/// session; activation assertions are skipped, with a reason, where the environment cannot activate at all.
/// </summary>
public class FocusTests
{
    public enum MainWindowState
    {
        Active,
        Minimized,
        HiddenInTray,
    }

    [Theory]
    [InlineData(MainWindowState.Active)]
    [InlineData(MainWindowState.Minimized)]
    [InlineData(MainWindowState.HiddenInTray)]
    public Task ManualCheckIn_PutsKeyboardFocusInTheSearchBox(MainWindowState state) => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var main = ShowMainWindow(ctx);
        var canActivate = main.IsActive;
        try
        {
            switch (state)
            {
                case MainWindowState.Minimized:
                    main.WindowState = WindowState.Minimized;
                    break;
                case MainWindowState.HiddenInTray:
                    main.Hide();
                    break;
            }

            await WpfTestHost.SettleAsync();
            ctx.Get<CheckInPresenter>().ShowManual();
            await WpfTestHost.SettleAsync(10);

            var popup = Assert.Single(Application.Current.Windows.OfType<CheckInWindow>());
            Assert.Same(popup.SearchBox, FocusManager.GetFocusedElement(popup));
            if (!canActivate)
            {
                popup.Close();
                Assert.Skip("This desktop session does not allow the test process to activate windows.");
            }

            Assert.True(popup.IsActive, "The manual check-in popup must be the active window.");
            Assert.Same(popup.SearchBox, Keyboard.FocusedElement);
            Assert.False(main.IsActive);
            popup.Close();
        }
        finally
        {
            main.Close();
        }
    });

    [Fact]
    public Task ScheduledPopup_InUnobtrusiveMode_DoesNotTakeFocus() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        Assert.Equal(PopupMode.Unobtrusive, ctx.Get<ISettingsStore>().Current.Tracking.PopupMode);
        var main = ShowMainWindow(ctx);
        try
        {
            if (!main.IsActive)
            {
                Assert.Skip("This desktop session does not allow the test process to activate windows.");
            }

            var outcome = ctx.Get<CheckInPresenter>().ShowPromptAsync(1, ctx.Time.GetUtcNow().UtcDateTime, PromptReason.Scheduled, CancellationToken.None);
            await WpfTestHost.SettleAsync(10);

            var popup = Assert.Single(Application.Current.Windows.OfType<CheckInWindow>());
            Assert.False(popup.IsActive, "An unobtrusive scheduled popup must not take focus.");
            Assert.True(main.IsActive);
            Assert.False(popup.IsKeyboardFocusWithin);

            ((CheckInViewModel)popup.DataContext).DismissCommand.Execute(null);
            Assert.Equal(PromptOutcome.Dismissed, await outcome);
        }
        finally
        {
            main.Close();
        }
    });

    [Fact]
    public Task ScheduledPopup_InFocusedMode_TakesFocus() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        await ctx.Get<ISettingsStore>().UpdateAsync(s => s.Tracking.PopupMode = PopupMode.Focused);
        var main = ShowMainWindow(ctx);
        try
        {
            var canActivate = main.IsActive;
            var outcome = ctx.Get<CheckInPresenter>().ShowPromptAsync(1, ctx.Time.GetUtcNow().UtcDateTime, PromptReason.Scheduled, CancellationToken.None);
            await WpfTestHost.SettleAsync(10);

            var popup = Assert.Single(Application.Current.Windows.OfType<CheckInWindow>());
            Assert.Same(popup.SearchBox, FocusManager.GetFocusedElement(popup));
            if (canActivate)
            {
                Assert.True(popup.IsActive);
                Assert.Same(popup.SearchBox, Keyboard.FocusedElement);
            }

            ((CheckInViewModel)popup.DataContext).DismissCommand.Execute(null);
            await outcome;
            if (!canActivate)
            {
                Assert.Skip("This desktop session does not allow the test process to activate windows.");
            }
        }
        finally
        {
            main.Close();
        }
    });

    [Fact]
    public Task Popup_DeclaresAltNumberShortcuts_ForRecentTasks() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<CheckInViewModel>();
        vm.Configure(null, null, PromptReason.Scheduled, null);
        var window = new CheckInWindow(vm, PopupPosition.BottomRight, takeFocus: false);
        try
        {
            var bindings = window.InputBindings.OfType<KeyBinding>()
                .Where(b => b.Modifiers == ModifierKeys.Alt && b.Key is >= Key.D1 and <= Key.D4)
                .OrderBy(b => b.Key)
                .ToList();

            Assert.Equal(4, bindings.Count);
            Assert.All(bindings, b => Assert.Same(vm.PickRecentByNumberCommand, b.Command));
            Assert.Equal(["1", "2", "3", "4"], bindings.Select(b => (string)b.CommandParameter));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AltNumber_WithoutRecentTasks_DoesNothing() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = ctx.Get<CheckInViewModel>();
        vm.Configure(null, null, PromptReason.Scheduled, null);
        await vm.LoadAsync();
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        Assert.False(vm.HasRecent);
        await vm.PickRecentByNumberCommand.ExecuteAsync("1");

        Assert.False(closed);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(await ctx.Get<ICheckInService>().GetLastObservationAsync());
    });

    [Fact]
    public Task AltNumber_WhileSearching_DoesNothing() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var (_, recent) = await SeedRecentAsync(ctx);
        var vm = ctx.Get<CheckInViewModel>();
        vm.Configure(null, null, PromptReason.Scheduled, null);
        await vm.LoadAsync();
        Assert.True(vm.HasRecent);
        Assert.Equal(recent.Id, vm.Recent[0].Task.Id);

        vm.SearchText = "something";
        Assert.False(vm.HasRecent);
        var before = await ctx.Get<ICheckInService>().GetLastObservationAsync();
        await vm.PickRecentByNumberCommand.ExecuteAsync("1");

        Assert.Equal(before!.Id, (await ctx.Get<ICheckInService>().GetLastObservationAsync())!.Id);
    });

    /// <summary>
    /// End to end with real keystrokes: another application is in the foreground, the global shortcut opens the
    /// popup in the foreground with the search box focused, and Alt+1 records recent task 1.
    /// Skipped where the CI desktop cannot deliver synthetic input or change the foreground window.
    /// </summary>
    [Fact]
    public Task GlobalShortcut_FromAnotherApplication_FocusesPopup_AndAltOneRecords() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var (_, recent) = await SeedRecentAsync(ctx);
        var presenter = ctx.Get<CheckInPresenter>();
        var hotkey = ctx.Get<GlobalHotkey>();
        hotkey.Pressed += (_, _) => presenter.ShowManual();
        // An unusual combination: Ctrl+Alt+Space itself is taken on the CI image. The code path is identical.
        if (!hotkey.Register("Ctrl+Alt+Shift+F11"))
        {
            Assert.Skip("The test shortcut is registered by another program on this machine.");
        }

        using var other = StartOtherApplication();
        try
        {
            var otherWindow = await WaitForValueAsync(() =>
            {
                other.Refresh();
                return other.MainWindowHandle != IntPtr.Zero ? other.MainWindowHandle : (IntPtr?)null;
            });
            if (otherWindow is not { } hwnd || !await BringToFrontAsync(hwnd))
            {
                Assert.Skip("Could not put another application in the foreground in this desktop session.");
            }

            if (!Win32Input.Chord(Win32Input.VkControl, Win32Input.VkMenu, Win32Input.VkShift, Win32Input.VkF11))
            {
                Assert.Skip("Synthetic keyboard input is not available in this desktop session.");
            }

            var popup = await WaitForAsync(() => Application.Current.Windows.OfType<CheckInWindow>().FirstOrDefault());
            if (popup is null)
            {
                Assert.Skip("The global shortcut was not delivered in this desktop session.");
            }

            await WpfTestHost.SettleAsync(10);
            var foreground = Win32Input.GetForegroundWindow();
            var who = foreground == new WindowInteropHelper(popup).Handle ? "popup"
                : foreground == otherWindow ? "other application"
                : foreground == IntPtr.Zero ? "none" : $"0x{foreground:X}";
            Assert.True(popup.IsActive, $"The popup opened by the global shortcut must be the active window (foreground: {who}, activation: {popup.LastActivation}).");
            Assert.Equal(new WindowInteropHelper(popup).Handle, Win32Input.GetForegroundWindow());
            Assert.Same(popup.SearchBox, Keyboard.FocusedElement);

            // Alt+1 records recent task 1 and closes the popup.
            await WaitForValueAsync(() => ((CheckInViewModel)popup.DataContext).HasRecent ? true : (bool?)null);
            Assert.True(Win32Input.Chord(Win32Input.VkMenu, Win32Input.Vk1));
            var closed = await WaitForValueAsync(() => popup.IsVisible ? (bool?)null : true);
            Assert.True(closed, "Alt+1 should record and close the popup.");
            var last = await ctx.Get<ICheckInService>().GetLastObservationAsync();
            Assert.Equal(recent.Id, last!.TaskId);
        }
        finally
        {
            hotkey.Dispose();
            foreach (var window in Application.Current.Windows.OfType<CheckInWindow>().ToList())
            {
                window.Close();
            }

            if (!other.HasExited)
            {
                other.Kill();
            }
        }
    });

    private static MainWindow ShowMainWindow(UiTestContext ctx)
    {
        var main = ctx.Get<MainWindow>();
        main.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        main.Show();
        main.Activate();
        Win32Input.SetForegroundWindow(new WindowInteropHelper(main).Handle);
        return main;
    }

    /// <summary>Two used tasks: the latest becomes "Same as before", the other is recent task 1.</summary>
    private static async Task<(TaskSummary Previous, TaskSummary Recent)> SeedRecentAsync(UiTestContext ctx)
    {
        var clientId = (await ctx.Get<IClientService>().GetClientsAsync())[0].Id;
        var tasks = ctx.Get<ITaskService>();
        var recent = await tasks.CreateAsync(new TaskInput(clientId, null, null, "Recent task", null, null));
        var previous = await tasks.CreateAsync(new TaskInput(clientId, null, null, "Previous task", null, null));
        var now = ctx.Time.GetUtcNow().UtcDateTime;
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, recent.Id, null, ObservedAtUtc: now.AddMinutes(-30)));
        ctx.Time.Advance(TimeSpan.FromMinutes(1));
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, previous.Id, null));
        return (previous, recent);
    }

    /// <summary>A trivial window in a separate process (Windows PowerShell + WinForms, present on all Windows).</summary>
    private static Process StartOtherApplication()
    {
        var script = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object System.Windows.Forms.Form; " +
                     "$f.Text = 'WorkTrace focus test'; $f.TopMost = $true; $f.ShowDialog()";
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return Process.Start(new ProcessStartInfo(powershell, $"-NoProfile -Command \"{script}\"") { UseShellExecute = false, CreateNoWindow = true })!;
    }

    private static async Task<bool> BringToFrontAsync(IntPtr hwnd)
    {
        for (var i = 0; i < 20; i++)
        {
            if (Win32Input.GetForegroundWindow() == hwnd)
            {
                return true;
            }

            Win32Input.SetForegroundWindow(hwnd);
            await Task.Delay(100);
        }

        return Win32Input.GetForegroundWindow() == hwnd;
    }

    private static async Task<T?> WaitForAsync<T>(Func<T?> probe, int timeoutMs = 5000)
        where T : class
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await WpfTestHost.SettleAsync(1);
        }

        return null;
    }

    private static async Task<T?> WaitForValueAsync<T>(Func<T?> probe, int timeoutMs = 5000)
        where T : struct
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await WpfTestHost.SettleAsync(1);
        }

        return null;
    }
}
