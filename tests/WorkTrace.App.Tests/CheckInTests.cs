using System.Windows;
using WorkTrace.App.Tracking;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Entities;
using WorkTrace.Core.Management;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tests;

/// <summary>Check-in popup: rendering in both modes and the keystroke-saving flows.</summary>
public class CheckInTests
{
    private static async Task<(UiTestContext Ctx, TaskSummary Invoice, TaskSummary Meeting)> SeedAsync()
    {
        var ctx = await UiTestContext.CreateAsync();
        var client = await ctx.Get<IClientService>().CreateAsync(new ClientInput("Contoso", null, false));
        var invoice = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(client.Id, null, "BUG-0042", "Invoice posting error", null, null));
        var internalId = (await ctx.Get<IClientService>().GetClientsAsync()).Single(c => c.Name == "Internal").Id;
        var meeting = await ctx.Get<ITaskService>().CreateAsync(new TaskInput(internalId, null, null, "Team meeting", null, null));
        return (ctx, invoice, meeting);
    }

    private static async Task<CheckInViewModel> OpenAsync(UiTestContext ctx, int? promptId = null, int? autoDismiss = null)
    {
        var vm = ctx.Get<CheckInViewModel>();
        vm.Configure(promptId, promptId is null ? null : ctx.Time.GetUtcNow().UtcDateTime, PromptReason.Scheduled, autoDismiss);
        await vm.LoadAsync();
        return vm;
    }

    [Theory]
    [InlineData(PopupMode.Unobtrusive)]
    [InlineData(PopupMode.Focused)]
    public Task Popup_RendersWithoutBindingErrors(PopupMode mode) => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, _) = await SeedAsync();
        await using var _ = ctx;
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, invoice.Id, null));
        WpfTestHost.BindingErrors.Take();
        WpfTestHost.UnhandledExceptions.Clear();

        var vm = await OpenAsync(ctx, promptId: null, autoDismiss: 120);
        var window = new CheckInWindow(vm, PopupPosition.BottomRight, takeFocus: mode == PopupMode.Focused);
        window.Show();
        try
        {
            await WpfTestHost.SettleAsync();
            Assert.True(vm.HasPrevious);

            vm.SearchText = "meet";
            await vm.WhenSearchSettledAsync();
            vm.SearchText = "something new";
            await vm.WhenSearchSettledAsync();
            vm.IsExpanded = true;
            await WpfTestHost.SettleAsync();

            // Every picker shows a selection ("Any client", "None", "Not specified" by default).
            var combos = FindAll<System.Windows.Controls.ComboBox>(window);
            Assert.Equal(3, combos.Count);
            Assert.All(combos, c => Assert.NotNull(c.SelectedItem));

            var area = SystemParameters.WorkArea;
            Assert.InRange(window.Left + window.ActualWidth, area.Left, area.Right + 1);
            Assert.InRange(window.Top + window.ActualHeight, area.Top, area.Bottom + 1);
        }
        finally
        {
            window.Close();
        }

        Assert.Empty(WpfTestHost.UnhandledExceptions);
        var errors = WpfTestHost.BindingErrors.Take();
        Assert.True(errors.Count == 0, "Binding errors:\n" + string.Join("\n", errors));
    });

    [Fact]
    public Task Enter_OnEmptySearch_RepeatsThePreviousTask() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, _) = await SeedAsync();
        await using var _ = ctx;
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, invoice.Id, null));
        var vm = await OpenAsync(ctx);
        PromptOutcome? outcome = null;
        vm.CloseRequested += (_, o) => outcome = o;

        Assert.Equal(invoice.Id, vm.PreviousTask?.Id);
        Assert.Contains("again", vm.PendingSummary, StringComparison.Ordinal);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(PromptOutcome.Answered, outcome);
        var last = await ctx.Get<ICheckInService>().GetLastObservationAsync();
        Assert.Equal(invoice.Id, last!.TaskId);
        Assert.Equal(ObservationOrigin.Manual, last.Origin);
    });

    [Fact]
    public Task TypingAndEnter_RecordsTheBestMatch() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, _) = await SeedAsync();
        await using var _ = ctx;
        var vm = await OpenAsync(ctx);

        vm.SearchText = "42";
        await vm.WhenSearchSettledAsync();
        Assert.Equal(invoice.Id, vm.SelectedSuggestion?.Task?.Id);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(invoice.Id, (await ctx.Get<ICheckInService>().GetLastObservationAsync())!.TaskId);
    });

    [Fact]
    public Task ArrowKeys_MoveThroughSuggestions() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, _, _) = await SeedAsync();
        await using var _ = ctx;
        var vm = await OpenAsync(ctx);

        vm.SearchText = "e"; // matches both tasks plus "create"
        await vm.WhenSearchSettledAsync();
        var first = vm.SelectedSuggestion;
        vm.MoveSelection(+1);
        Assert.NotEqual(first, vm.SelectedSuggestion);
        vm.MoveSelection(-5);
        Assert.Equal(first, vm.SelectedSuggestion);
    });

    [Fact]
    public Task UnknownText_CreatesTheTask_ForThePreviousClient() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, _) = await SeedAsync();
        await using var _ = ctx;
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, invoice.Id, null));
        var vm = await OpenAsync(ctx);

        vm.SearchText = "Year-end closing support";
        vm.ExternalId = "CRM-77";
        await vm.WhenSearchSettledAsync();
        Assert.True(vm.SelectedSuggestion?.IsCreateNew);
        Assert.Contains("Contoso", vm.PendingSummary, StringComparison.Ordinal);

        await vm.SaveCommand.ExecuteAsync(null);

        var created = (await ctx.Get<ITaskService>().SearchAsync(new TaskQuery("Year-end"))).Single();
        Assert.Equal("Contoso", created.ClientName);
        Assert.Equal("CRM-77", created.ExternalId);
        Assert.Equal(created.Id, (await ctx.Get<ICheckInService>().GetLastObservationAsync())!.TaskId);
    });

    [Fact]
    public Task AltNumber_RecordsARecentTask() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, meeting) = await SeedAsync();
        await using var _ = ctx;
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, meeting.Id, null, ObservedAtUtc: ctx.Time.GetUtcNow().UtcDateTime.AddMinutes(-60)));
        await ctx.Get<ICheckInService>().RecordAsync(new CheckInRequest(null, invoice.Id, null));
        var vm = await OpenAsync(ctx);

        // The previous task (invoice) is offered separately; recents start with the meeting.
        var recent = Assert.Single(vm.Recent, r => r.Number == 1);
        Assert.Equal(meeting.Id, recent.Task.Id);
        await vm.PickRecentByNumberCommand.ExecuteAsync("1");

        Assert.Equal(meeting.Id, (await ctx.Get<ICheckInService>().GetLastObservationAsync())!.TaskId);
    });

    [Fact]
    public Task NoteOnly_IsAllowed_ButEmptyIsNot() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = await OpenAsync(ctx);
        Assert.False(vm.HasPrevious);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);

        vm.Note = "Helping a colleague";
        await vm.SaveCommand.ExecuteAsync(null);
        var last = await ctx.Get<ICheckInService>().GetLastObservationAsync();
        Assert.Null(last!.TaskId);
        Assert.Equal("Helping a colleague", last.Note);
    });

    [Fact]
    public Task InvalidTime_ShowsError_AndOpensDetails() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var vm = await OpenAsync(ctx);
        vm.Note = "x";
        vm.ObservedTimeText = "25:99";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsExpanded);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(await ctx.Get<ICheckInService>().GetLastObservationAsync());
    });

    [Fact]
    public Task Countdown_ClosesAsTimedOut_UnlessTheUserInteracts() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var ignored = await OpenAsync(ctx, autoDismiss: 3);
        PromptOutcome? outcome = null;
        ignored.CloseRequested += (_, o) => outcome = o;
        ignored.Tick();
        ignored.Tick();
        Assert.Null(outcome);
        ignored.Tick();
        Assert.Equal(PromptOutcome.TimedOut, outcome);

        var touched = await OpenAsync(ctx, autoDismiss: 2);
        PromptOutcome? touchedOutcome = null;
        touched.CloseRequested += (_, o) => touchedOutcome = o;
        touched.NotifyInteraction();
        touched.Tick();
        touched.Tick();
        touched.Tick();
        Assert.Null(touchedOutcome);
        Assert.False(touched.IsCountdownActive);
    });

    [Fact]
    public Task AnsweringAScheduledPrompt_MarksItAnswered() => WpfTestHost.RunAsync(async () =>
    {
        var (ctx, invoice, _) = await SeedAsync();
        await using var _ = ctx;
        var now = ctx.Time.GetUtcNow().UtcDateTime;
        var promptId = await ctx.Get<ITrackingStore>().CreatePromptAsync(now, now, CheckInOutcome.Pending);
        var vm = await OpenAsync(ctx, promptId);

        vm.SearchText = "invoice";
        await vm.WhenSearchSettledAsync();
        await vm.SaveCommand.ExecuteAsync(null);

        var last = await ctx.Get<ICheckInService>().GetLastObservationAsync();
        Assert.Equal(ObservationOrigin.Scheduled, last!.Origin);
        var timeline = await ctx.Get<ICheckInService>().GetDayTimelineAsync(DateOnly.FromDateTime(now), TimeZoneInfo.Utc);
        Assert.DoesNotContain(timeline, e => e.Kind == TimelineEventKind.MissedCheckIn);
    });

    [Fact]
    public Task Presenter_ShowsOnePopup_AndReportsTheOutcome() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var presenter = ctx.Get<CheckInPresenter>();

        var before = UiTestContext.Popups();
        var outcomeTask = presenter.ShowPromptAsync(1, DateTime.UtcNow, PromptReason.Scheduled, CancellationToken.None);
        await WpfTestHost.SettleAsync();
        Assert.True(presenter.IsOpen);

        presenter.ShowManual(); // brings the open popup forward instead of opening a second one
        var window = Assert.Single(UiTestContext.Popups().Except(before));
        ((CheckInViewModel)window.DataContext).DismissCommand.Execute(null);
        Assert.Equal(PromptOutcome.Dismissed, await outcomeTask);
        Assert.False(presenter.IsOpen);
    });

    [Fact]
    public Task Presenter_ClosesPopup_WhenCancelled() => WpfTestHost.RunAsync(async () =>
    {
        await using var ctx = await UiTestContext.CreateAsync();
        var presenter = ctx.Get<CheckInPresenter>();
        using var cts = new CancellationTokenSource();

        var outcomeTask = presenter.ShowPromptAsync(1, DateTime.UtcNow, PromptReason.Scheduled, cts.Token);
        await WpfTestHost.SettleAsync();
        await cts.CancelAsync();

        Assert.Equal(PromptOutcome.Closed, await outcomeTask);
    });

    private static List<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        var found = new List<T>();
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(FindAll<T>(child));
        }

        return found;
    }
}
