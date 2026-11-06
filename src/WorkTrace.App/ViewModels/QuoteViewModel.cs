using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// The perspective quote above the task list. One quote at a time, in shuffled order without repeats until all have
/// been shown; a new one about once an hour while WorkTrace runs, or on request. A singleton, so the quote stays the
/// same when you move between pages.
/// </summary>
public sealed partial class QuoteViewModel : ObservableObject
{
    private static readonly TimeSpan RotateEvery = TimeSpan.FromHours(1);
    private readonly DispatcherTimer _timer;
    private List<int> _order;
    private int _position;

    public QuoteViewModel()
    {
        _order = Shuffled(previous: null);
        CurrentQuote = PerspectiveQuotes.At(_order[0]);
        _timer = new DispatcherTimer { Interval = RotateEvery };
        _timer.Tick += (_, _) => Advance();
        _timer.Start();
    }

    [ObservableProperty]
    public partial string CurrentQuote { get; private set; }

    /// <summary>Shows another quote now (and restarts the hour).</summary>
    [RelayCommand]
    private void Next()
    {
        Advance();
        _timer.Stop();
        _timer.Start();
    }

    private void Advance()
    {
        _position++;
        if (_position >= _order.Count)
        {
            _order = Shuffled(previous: _order[^1]);
            _position = 0;
        }

        CurrentQuote = PerspectiveQuotes.At(_order[_position]);
    }

    /// <summary>A new random order that never starts with the quote that was just shown.</summary>
    private static List<int> Shuffled(int? previous)
    {
        var order = Enumerable.Range(0, PerspectiveQuotes.All.Count).OrderBy(_ => Random.Shared.Next()).ToList();
        if (previous is { } last && order[0] == last && order.Count > 1)
        {
            (order[0], order[^1]) = (order[^1], order[0]);
        }

        return order;
    }
}
