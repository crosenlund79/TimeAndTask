using System.ComponentModel;
using System.Windows;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Views;

/// <summary>
/// Mini Mode: the Overview's Today panel in a small, movable, resizable window that stays on top by default. Window
/// behaviour only; the data and commands are the shared <see cref="HomeViewModel"/>.
/// </summary>
public partial class MiniWindow : Window
{
    public MiniWindow(HomeViewModel viewModel, MiniWindowSettings placement)
    {
        DataContext = viewModel;
        InitializeComponent();
        ApplyPlacement(placement);
        var topmost = DependencyPropertyDescriptor.FromProperty(TopmostProperty, typeof(Window));
        topmost.AddValueChanged(this, OnTopmostChanged);
        Closed += (_, _) => topmost.RemoveValueChanged(this, OnTopmostChanged);
    }

    private void OnTopmostChanged(object? sender, EventArgs e) => PinChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The window was closed by the user (Alt+F4, taskbar): return to the normal window instead.</summary>
    public event EventHandler? RestoreRequested;

    /// <summary>Always-on-top was switched (remembered).</summary>
    public event EventHandler? PinChanged;

    /// <summary>Set by the window manager when Mini Mode ends or the application exits.</summary>
    internal bool AllowClose { get; set; }

    /// <summary>Where the window is now, for the next session.</summary>
    public MiniWindowSettings Placement
    {
        get
        {
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            return new MiniWindowSettings { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height, Topmost = Topmost };
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!AllowClose)
        {
            e.Cancel = true;
            RestoreRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The remembered size and position when they are still on a screen; otherwise the bottom right of the work area.</summary>
    private void ApplyPlacement(MiniWindowSettings placement)
    {
        Topmost = placement.Topmost;
        if (placement.Width >= MinWidth && placement.Height >= MinHeight)
        {
            Width = placement.Width;
            Height = placement.Height;
        }

        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (placement.Left is { } left && placement.Top is { } top && screen.IntersectsWith(new Rect(left, top, Width, 40)) && screen.Contains(new Point(left + 40, top + 10)))
        {
            Left = left;
            Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 16;
        }
    }
}
