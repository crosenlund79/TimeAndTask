using System.Media;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WorkTrace.App.ViewModels;
using WorkTrace.App.Views;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Tracking;

/// <summary>
/// Owns the single check-in popup. Scheduled prompts honour the configured popup mode; manual
/// check-ins (tray, global shortcut) always take focus because the user asked for them.
/// </summary>
public sealed class CheckInPresenter(IServiceProvider services, ISettingsStore settings, ILogger<CheckInPresenter> logger) : ICheckInPresenter
{
    private CheckInWindow? _window;
    private volatile bool _isOpen;

    /// <summary>Raised on the UI thread after an observation was recorded from a popup.</summary>
    public event EventHandler<ObservationSummary>? Recorded;

    public bool IsOpen => _isOpen;

    public Task<PromptOutcome> ShowPromptAsync(int promptId, DateTime scheduledAtUtc, PromptReason reason, CancellationToken cancellationToken) =>
        Dispatcher.InvokeAsync(() =>
        {
            var tracking = settings.Current.Tracking;
            var window = Open(promptId, scheduledAtUtc, reason, tracking.AutoDismissSeconds, takeFocus: tracking.PopupMode == PopupMode.Focused);
            if (tracking.ReminderSound)
            {
                SystemSounds.Asterisk.Play();
            }

            var completion = new TaskCompletionSource<PromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => completion.TrySetResult(window.Outcome);
            var registration = cancellationToken.Register(() => Dispatcher.BeginInvoke(() => window.CloseWith(PromptOutcome.Closed)));
            completion.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            return completion.Task;
        }).Task.Unwrap();

    /// <summary>Opens a manual check-in, or brings the open popup forward. Call on the UI thread.</summary>
    public void ShowManual()
    {
        if (_window is not null)
        {
            _window.ActivateForInput();
            return;
        }

        Open(null, null, PromptReason.Scheduled, autoDismissSeconds: null, takeFocus: true);
    }

    public void CloseAll() => Dispatcher.Invoke(() => _window?.CloseWith(PromptOutcome.Closed));

    private static System.Windows.Threading.Dispatcher Dispatcher => Application.Current.Dispatcher;

    private CheckInWindow Open(int? promptId, DateTime? scheduledAtUtc, PromptReason reason, int? autoDismissSeconds, bool takeFocus)
    {
        var viewModel = services.GetRequiredService<CheckInViewModel>();
        viewModel.Configure(promptId, scheduledAtUtc, reason, autoDismissSeconds);
        viewModel.Recorded += (_, observation) => Recorded?.Invoke(this, observation);

        var window = new CheckInWindow(viewModel, settings.Current.Tracking.PopupPosition, takeFocus);
        _window = window;
        _isOpen = true;
        window.Closed += (_, _) =>
        {
            _window = null;
            _isOpen = false;
            viewModel.Dispose();
        };

        window.Show();
        _ = LoadAsync(viewModel);
        logger.LogDebug("Check-in popup opened ({Kind})", promptId is null ? "manual" : "scheduled");
        return window;
    }

    private async Task LoadAsync(CheckInViewModel viewModel)
    {
        try
        {
            await viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loading the check-in popup failed");
        }
    }
}
