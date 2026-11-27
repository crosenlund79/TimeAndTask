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

    /// <summary>Open the item in its editor (minor task reminders).</summary>
    Edit = 3,

    /// <summary>Stop a recurring minor task's reminders.</summary>
    Disable = 4,
}

/// <summary>
/// Compact "have you registered your time?" popup (bottom right, never steals focus). Window behaviour only:
/// the answer goes to <see cref="Tracking.TimeReminderService"/> through <see cref="Answer"/>.
/// </summary>
public partial class TimeReminderWindow : Window
{
    private readonly TaskCompletionSource<ReminderAnswer> _answer = new();
    private readonly DispatcherTimer _autoClose;

    /// <param name="yesNo">Shutdown style: only Yes / No (labels replaceable through <paramref name="labels"/>).</param>
    /// <param name="showEdit">Adds an Edit button (minor task reminders).</param>
    /// <param name="showDisable">Adds a Disable reminder button (recurring minor tasks).</param>
    public TimeReminderWindow(string heading, string message, bool yesNo, TimeSpan autoClose, (string Yes, string No, string Caption)? labels = null,
        bool showEdit = false, bool showDisable = false)
    {
        InitializeComponent();
        EditButton.Visibility = showEdit ? Visibility.Visible : Visibility.Collapsed;
        DisableButton.Visibility = showDisable ? Visibility.Visible : Visibility.Collapsed;
        HeadingText.Text = heading;
        MessageText.Text = message;
        if (labels is { } l)
        {
            YesButton.Content = l.Yes;
            NoButton.Content = l.No;
            CaptionRun.Text = l.Caption;
            Title = "WorkTrace" + l.Caption.Replace(" · ", " – ", StringComparison.Ordinal);
        }

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

    private Action? _openTimesheet;
    private Func<Task<string>>? _copyStatus;

    /// <summary>
    /// Weekly reporting reminder: Open Timesheet / Copy next week's status (both keep the popup open) / Remind me later /
    /// Done for this week. <paramref name="copyStatus"/> returns the confirmation shown in the popup.
    /// </summary>
    public void UseWeeklyActions(Action openTimesheet, Func<Task<string>> copyStatus)
    {
        _openTimesheet = openTimesheet;
        _copyStatus = copyStatus;
        PeriodicButtons.Visibility = Visibility.Collapsed;
        ShutdownButtons.Visibility = Visibility.Collapsed;
        WeeklyButtons.Visibility = Visibility.Visible;
        CaptionRun.Text = " · weekly reporting";
        Title = "WorkTrace – weekly reporting";
    }

    private void OnOpenTimesheet(object sender, RoutedEventArgs e)
    {
        RestartAutoClose();
        _openTimesheet?.Invoke();
    }

    private async void OnCopyStatus(object sender, RoutedEventArgs e)
    {
        RestartAutoClose();
        if (_copyStatus is null)
        {
            return;
        }

        string text;
        try
        {
            text = await _copyStatus();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            text = "Could not copy the status: " + ex.Message;
        }

        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void RestartAutoClose()
    {
        _autoClose.Stop();
        _autoClose.Start();
    }

    private void Finish(ReminderAnswer answer)
    {
        _autoClose.Stop();
        _answer.TrySetResult(answer);
        Close();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Done);

    private void OnNotYet(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.NotYet);

    private void OnTomorrow(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Tomorrow);

    private void OnEdit(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Edit);

    private void OnDisable(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.Disable);

    private void OnClose(object sender, RoutedEventArgs e) => Finish(ReminderAnswer.NotYet);

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
