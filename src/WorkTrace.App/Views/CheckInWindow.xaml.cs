using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using WorkTrace.App.Interop;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Settings;
using WorkTrace.Core.Tracking;

namespace WorkTrace.App.Views;

/// <summary>
/// Compact check-in popup. Window behaviour only: placement, focus handling, the countdown tick and
/// keyboard routing between the search box and the result list.
/// </summary>
public partial class CheckInWindow : Window
{
    private const double ScreenMargin = 16;

    private readonly CheckInViewModel _viewModel;
    private readonly bool _takeFocus;
    private readonly DispatcherTimer _tick;
    private readonly ILogger? _logger;
    private bool _swallowAltCode;

    public CheckInWindow(CheckInViewModel viewModel, bool takeFocus, ILogger? logger = null)
    {
        _logger = logger;
        if (CaptureCreationTraces)
        {
            CreationTrace = Environment.StackTrace;
        }

        _viewModel = viewModel;
        _takeFocus = takeFocus;
        DataContext = viewModel;
        InitializeComponent();

        // Unobtrusive mode: appear without taking keyboard focus from the user's current window.
        ShowActivated = takeFocus;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            _viewModel.Tick();
            if (!_viewModel.IsCountdownActive)
            {
                _tick.Stop(); // nothing left to count down
            }
        };

        viewModel.CloseRequested += OnCloseRequested;
        SourceInitialized += (_, _) => NativeMethods.StylePopup(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
        Activated += (_, _) => FocusSearchIfNeeded();
        PreviewKeyDown += OnWindowPreviewKeyDown;
        PreviewTextInput += OnWindowPreviewTextInput;
        PreviewMouseDown += (_, _) => _viewModel.NotifyInteraction();
        MouseEnter += (_, _) => _viewModel.SetHovered(true);
        MouseLeave += (_, _) => _viewModel.SetHovered(false);
        Closed += (_, _) =>
        {
            _tick.Stop();
            _viewModel.CloseRequested -= OnCloseRequested;
        };
    }

    /// <summary>How the popup ended; Dismissed unless the view model says otherwise.</summary>
    public PromptOutcome Outcome { get; private set; } = PromptOutcome.Dismissed;

    /// <summary>
    /// Brings the popup to the front with keyboard focus in the search box (global shortcut, tray, focused
    /// mode). Windows may refuse a foreground change the first time (foreground lock), so the result is
    /// checked once more after the activation has settled.
    /// </summary>
    public void ActivateForInput()
    {
        // Not an interaction: a focused-mode scheduled popup activates itself and must keep its countdown.
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        var first = TryActivate();
        FocusSearchBox();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var second = IsActive ? NativeMethods.ForegroundResult.AlreadyForeground : TryActivate();
            FocusSearchBox();
            LastActivation = second;
            _logger?.LogInformation(
                "Check-in popup activation: first={First}, retry={Retry}, active={Active}",
                first, second, IsActive);
        });
    }

    /// <summary>Tests only: record where each popup was created, to diagnose unexpected extra popups.</summary>
    internal static bool CaptureCreationTraces { get; set; }

    internal string? CreationTrace { get; }

    /// <summary>Outcome of the most recent activation attempt (diagnostics and tests).</summary>
    internal NativeMethods.ForegroundResult? LastActivation { get; private set; }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    private NativeMethods.ForegroundResult TryActivate()
    {
        var result = NativeMethods.ForceForeground(Handle);
        Activate();
        return result;
    }

    /// <summary>Closes with a specific outcome (application exit).</summary>
    public void CloseWith(PromptOutcome outcome)
    {
        Outcome = outcome;
        Close();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOnScreen();
        if (_viewModel.IsCountdownActive)
        {
            _tick.Start();
        }
        if (_takeFocus)
        {
            ActivateForInput();
        }
    }

    /// <summary>
    /// Alt+1…4 (top row or number pad) records the recent task with that number. Handled here, before the
    /// focused search box sees the keys, so the shortcut works wherever focus is and never types into it.
    /// </summary>
    /// <returns>True when the keys were the shortcut (handled even if there is no such recent task).</returns>
    internal bool TryHandleRecentShortcut(Key key, ModifierKeys modifiers)
    {
        if (modifiers != ModifierKeys.Alt)
        {
            return false;
        }

        var number = key switch
        {
            >= Key.D1 and <= Key.D4 => key - Key.D1 + 1,
            >= Key.NumPad1 and <= Key.NumPad4 => key - Key.NumPad1 + 1,
            _ => 0,
        };
        if (number == 0)
        {
            return false;
        }

        // Alt+number pad also makes Windows type an Alt code character when Alt is released.
        _swallowAltCode = key >= Key.NumPad1;
        var parameter = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_viewModel.PickRecentByNumberCommand.CanExecute(parameter))
        {
            _viewModel.PickRecentByNumberCommand.Execute(parameter);
        }

        return true;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        _viewModel.NotifyInteraction();
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is not (Key.LeftAlt or Key.RightAlt))
        {
            _swallowAltCode = false;
        }

        if (TryHandleRecentShortcut(key, Keyboard.Modifiers))
        {
            e.Handled = true;
        }
    }

    private void OnWindowPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_swallowAltCode)
        {
            _swallowAltCode = false;
            e.Handled = true;
        }
    }

    private void OnCloseRequested(object? sender, PromptOutcome outcome)
    {
        Outcome = outcome;
        Close();
    }

    /// <summary>
    /// Like the reminder popups: in the corner next to the notification area (bottom right; top or left when the taskbar
    /// is there) of the monitor in use (foreground window, else mouse pointer), inside its work area. Placed while the
    /// popup opens and again once by <see cref="SettleLayout"/>; after that it never moves by itself.
    /// </summary>
    private void PlaceOnScreen()
    {
        if (_settled)
        {
            return;
        }

        var (area, taskbarTop, taskbarLeft) = MonitorWorkArea();
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : 320;
        var left = taskbarLeft ? area.Left + ScreenMargin : area.Right - width - ScreenMargin;
        var top = taskbarTop ? area.Top + ScreenMargin : area.Bottom - height - ScreenMargin;
        Left = Math.Max(area.Left, Math.Min(left, area.Right - width));
        Top = Math.Max(area.Top, Math.Min(top, area.Bottom - height));
    }

    /// <summary>
    /// Called once the popup's content has loaded: fixes its size (at most the work area) and its final position, so
    /// typing, opening a list, validation messages or refreshed suggestions never move or resize it. Growth after this
    /// (e.g. "More details") scrolls inside the popup. The user can still drag it by the header.
    /// </summary>
    public void SettleLayout()
    {
        if (_settled || !IsLoaded)
        {
            return;
        }

        UpdateLayout();
        var (area, _, _) = MonitorWorkArea();
        var height = Math.Min(ActualHeight, area.Height - (2 * ScreenMargin));
        SizeToContent = SizeToContent.Manual;
        Height = height;
        PlaceOnScreen();
        _settled = true;
    }

    private bool _settled;

    /// <summary>The active monitor's work area in device-independent units (the primary one if unavailable) and the taskbar edge.</summary>
    /// <remarks>Decided once per popup, so growing or clicking it never moves it to another screen.</remarks>
    private (Rect Area, bool TaskbarTop, bool TaskbarLeft) MonitorWorkArea()
    {
        if (_workArea is { } known)
        {
            return known;
        }

        if (NativeMethods.ActiveMonitor(new WindowInteropHelper(this).Handle) is { } monitor)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var work = monitor.Work;
            var area = new Rect(work.Left / dpi.DpiScaleX, work.Top / dpi.DpiScaleY, (work.Right - work.Left) / dpi.DpiScaleX, (work.Bottom - work.Top) / dpi.DpiScaleY);
            _workArea = (area, work.Top > monitor.Bounds.Top, work.Left > monitor.Bounds.Left);
            return _workArea.Value;
        }

        return (SystemParameters.WorkArea, false, false);
    }

    private (Rect Area, bool TaskbarTop, bool TaskbarLeft)? _workArea;

    /// <summary>
    /// After activation WPF focuses the window itself, not a control; then typing goes nowhere. Move focus
    /// to the search box unless the user is already in one of the popup's inputs (e.g. clicked the note box).
    /// </summary>
    private void FocusSearchIfNeeded()
    {
        if (!IsUserInputFocused())
        {
            FocusSearchBox();
        }
    }

    private bool IsUserInputFocused() =>
        Keyboard.FocusedElement is DependencyObject focused
        && !ReferenceEquals(focused, this)
        && IsAncestorOf(focused)
        && focused is TextBoxBase or ComboBox or ComboBoxItem or DatePicker or ButtonBase or ListBoxItem or Expander or ToggleButton;

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!IsUserInputFocused())
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        });
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(+1);
                SuggestionList.ScrollIntoView(_viewModel.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Up:
                _viewModel.MoveSelection(-1);
                SuggestionList.ScrollIntoView(_viewModel.SelectedSuggestion);
                e.Handled = true;
                break;
            case Key.Return when Keyboard.Modifiers == ModifierKeys.None:
                if (_viewModel.SaveCommand.CanExecute(null))
                {
                    _viewModel.SaveCommand.Execute(null);
                }

                e.Handled = true;
                break;
        }
    }

    private void OnSuggestionDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedSuggestion is not null && _viewModel.SaveCommand.CanExecute(null))
        {
            _viewModel.SaveCommand.Execute(null);
        }
    }
}
