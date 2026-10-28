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
    private readonly PopupPosition _position;
    private readonly bool _takeFocus;
    private readonly DispatcherTimer _tick;
    private readonly ILogger? _logger;

    public CheckInWindow(CheckInViewModel viewModel, PopupPosition position, bool takeFocus, ILogger? logger = null)
    {
        _logger = logger;
        _viewModel = viewModel;
        _position = position;
        _takeFocus = takeFocus;
        DataContext = viewModel;
        InitializeComponent();

        // Unobtrusive mode: appear without taking keyboard focus from the user's current window.
        ShowActivated = takeFocus;

        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => _viewModel.Tick();

        viewModel.CloseRequested += OnCloseRequested;
        SourceInitialized += (_, _) => NativeMethods.StylePopup(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
        SizeChanged += (_, _) => PlaceOnScreen();
        Activated += (_, _) => FocusSearchIfNeeded();
        PreviewKeyDown += (_, _) => _viewModel.NotifyInteraction();
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
        _viewModel.NotifyInteraction();
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
        _tick.Start();
        if (_takeFocus)
        {
            ActivateForInput();
        }
    }

    private void OnCloseRequested(object? sender, PromptOutcome outcome)
    {
        Outcome = outcome;
        Close();
    }

    private void PlaceOnScreen()
    {
        var area = SystemParameters.WorkArea;
        var height = ActualHeight > 0 ? ActualHeight : 320;
        Left = _position == PopupPosition.Center
            ? area.Left + ((area.Width - Width) / 2)
            : area.Right - Width - ScreenMargin;
        Top = _position switch
        {
            PopupPosition.TopRight => area.Top + ScreenMargin,
            PopupPosition.Center => area.Top + ((area.Height - height) / 2),
            _ => area.Bottom - height - ScreenMargin,
        };
    }

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
