using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WorkTrace.App.Views;

public enum ReminderAnswer
{
    /// <summary>Closed, ignored or "No": registration still outstanding.</summary>
    NotYet = 0,
    Done = 1,
    Tomorrow = 2,
}

/// <summary>
/// Compact "have you registered your time?" popup (bottom right, never steals focus). Window behaviour only:
/// the answer goes to <see cref="Tracking.TimeReminderService"/> through <see cref="Answer"/>.
/// </summary>
public partial class TimeReminderWindow : Window
{
    private readonly TaskCompletionSource<ReminderAnswer> _answer = new();
    private readonly DispatcherTimer _autoClose;

    /// <param name="yesNo">Shutdown style: only Yes / No.</param>
    public TimeReminderWindow(string heading, string message, bool yesNo, TimeSpan autoClose)
    {
        InitializeComponent();
        HeadingText.Text = heading;
        MessageText.Text = message;
        if (yesNo)
        {
            PeriodicButtons.Visibility = Visibility.Collapsed;
            ShutdownButtons.Visibility = Visibility.Visible;
        }

        _autoClose = new DispatcherTimer { Interval = autoClose };
        _autoClose.Tick += (_, _) => Finish(ReminderAnswer.NotYet);
        Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 16;
            Top = area.Bottom - ActualHeight - 16;
            _autoClose.Start();
        };
        Closed += (_, _) => _answer.TrySetResult(ReminderAnswer.NotYet);
    }

    public Task<ReminderAnswer> Answer => _answer.Task;

    private void Finish(ReminderAnswer answer)
    {
        _autoClose.Stop();
        _answer.TrySetResult(answer);
        Close();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Done);

    private void OnNotYet(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.NotYet);

    private void OnTomorrow(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Tomorrow);

    private void OnClose(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.NotYet);

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
