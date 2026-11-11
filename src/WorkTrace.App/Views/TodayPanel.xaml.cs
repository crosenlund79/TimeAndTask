using System.Windows;
using System.Windows.Controls;

namespace WorkTrace.App.Views;

/// <summary>
/// The Overview "Today" panel (the day's registrations, task history and check-ins). Used by the Overview and by Mini
/// Mode with the same <see cref="ViewModels.HomeViewModel"/>, so both always show the same data and commands.
/// </summary>
public partial class TodayPanel : UserControl
{
    /// <summary>Compact layout for Mini Mode: the day navigation goes under the title and the Mini Mode button is hidden.</summary>
    public static readonly DependencyProperty IsCompactProperty =
        DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(TodayPanel), new PropertyMetadata(false));

    public TodayPanel() => InitializeComponent();

    public bool IsCompact
    {
        get => (bool)GetValue(IsCompactProperty);
        set => SetValue(IsCompactProperty, value);
    }
}
