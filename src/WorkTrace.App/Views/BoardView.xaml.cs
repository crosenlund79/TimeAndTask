using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WorkTrace.App.ViewModels;
using WorkTrace.Core.Entities;

namespace WorkTrace.App.Views;

/// <summary>
/// The Kanban board. Code-behind is window behaviour only: drag-and-drop gestures, the insertion marker,
/// keeping one selection across the five column lists, and keyboard focus. Moves go through the view model.
/// </summary>
public partial class BoardView : UserControl
{
    private readonly List<ListBox> _columns = [];
    private BoardViewModel? _viewModel;
    private Point? _dragStart;
    private CardItemViewModel? _dragCard;
    private InsertionAdorner? _marker;
    private bool _syncingSelection;

    public BoardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as BoardViewModel);
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(BoardViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BoardPageViewModel.HasDetail) && _viewModel is { HasDetail: false })
        {
            ClearSelection(except: null);
        }
    }

    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            Capture.FocusBox();
            e.Handled = true;
        }
    }

    private void OnFind(object sender, ExecutedRoutedEventArgs e) => ListNavigation.FocusSoon(SearchBox, selectAll: true);

    private void OnColumnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox list && !_columns.Contains(list))
        {
            _columns.Add(list);
            list.Unloaded += (_, _) => _columns.Remove(list);
        }
    }

    // ---- Selection (one card across all columns) ----

    private void OnColumnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || _viewModel is null || sender is not ListBox list || list.SelectedItem is not CardItemViewModel card)
        {
            return;
        }

        ClearSelection(except: list);
        _viewModel.SelectedCard = card;
    }

    private void ClearSelection(ListBox? except)
    {
        _syncingSelection = true;
        foreach (var other in _columns.Where(c => !ReferenceEquals(c, except)))
        {
            other.SelectedItem = null;
        }

        _syncingSelection = false;
    }

    // ---- Keyboard: Alt+arrows move the selected card ----

    private async void OnColumnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null || sender is not ListBox { SelectedItem: CardItemViewModel card } || Keyboard.Modifiers != ModifierKeys.Alt)
        {
            return;
        }

        var (dx, dy) = e.SystemKey switch
        {
            Key.Left => (-1, 0),
            Key.Right => (1, 0),
            Key.Up => (0, -1),
            Key.Down => (0, 1),
            _ => (0, 0),
        };
        if (dx == 0 && dy == 0)
        {
            return;
        }

        e.Handled = true;
        await _viewModel.MoveByKeyAsync(card, dx, dy);
        FocusCard(card.Id);
    }

    private void FocusCard(int cardId)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            foreach (var list in _columns)
            {
                if (list.Items.OfType<CardItemViewModel>().FirstOrDefault(c => c.Id == cardId) is { } item)
                {
                    _syncingSelection = true;
                    ClearSelection(except: list);
                    list.SelectedItem = item;
                    _syncingSelection = false;
                    list.UpdateLayout();
                    (list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem)?.Focus();
                    return;
                }
            }
        });
    }

    // ---- Drag-and-drop ----

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _dragCard = null;
        if (sender is ListBox list && ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: CardItemViewModel card })
        {
            _dragStart = e.GetPosition(this);
            _dragCard = card;
        }
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _dragCard is not { } card || e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list)
        {
            return;
        }

        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragStart = null;
        var data = new DataObject(BoardDragDrop.Format, card.Id);
        try
        {
            DragDrop.DoDragDrop(list, data, DragDropEffects.Move);
        }
        finally
        {
            RemoveMarker();
            _dragCard = null;
        }
    }

    private void OnColumnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox list || !e.Data.GetDataPresent(BoardDragDrop.Format))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        var (_, markerY) = Target(list, e.GetPosition(list));
        ShowMarker(list, markerY);
    }

    private void OnColumnDragLeave(object sender, DragEventArgs e) => RemoveMarker();

    private async void OnColumnDrop(object sender, DragEventArgs e)
    {
        RemoveMarker();
        if (_viewModel is null || sender is not ListBox { Tag: BoardStatus status } list || e.Data.GetData(BoardDragDrop.Format) is not int cardId)
        {
            return;
        }

        e.Handled = true;
        var (beforeId, _) = Target(list, e.GetPosition(list));
        await _viewModel.MoveCardAsync(cardId, status, beforeId);
        FocusCard(cardId);
    }

    /// <summary>The card to insert before (null = end of the column) and where to draw the marker.</summary>
    private static (int? BeforeId, double MarkerY) Target(ListBox list, Point position)
    {
        var rows = new List<(int Id, double Top, double Height)>();
        foreach (var card in list.Items.OfType<CardItemViewModel>())
        {
            if (list.ItemContainerGenerator.ContainerFromItem(card) is ListBoxItem container && container.IsVisible)
            {
                var top = container.TranslatePoint(new Point(0, 0), list).Y;
                rows.Add((card.Id, top, container.ActualHeight));
            }
        }

        var index = BoardDragDrop.InsertionIndex([.. rows.Select(r => (r.Top, r.Height))], position.Y);
        return index < rows.Count
            ? (rows[index].Id, rows[index].Top - 3)
            : (null, rows.Count == 0 ? 4 : rows[^1].Top + rows[^1].Height + 1);
    }

    private void ShowMarker(ListBox list, double y)
    {
        var layer = AdornerLayer.GetAdornerLayer(list);
        if (layer is null)
        {
            return;
        }

        if (_marker is null || !ReferenceEquals(_marker.AdornedElement, list))
        {
            RemoveMarker();
            _marker = new InsertionAdorner(list);
            layer.Add(_marker);
        }

        _marker.Y = y;
    }

    private void RemoveMarker()
    {
        if (_marker is not null)
        {
            AdornerLayer.GetAdornerLayer(_marker.AdornedElement)?.Remove(_marker);
            _marker = null;
        }
    }

    /// <summary>A horizontal accent line showing where a dragged card will land.</summary>
    private sealed class InsertionAdorner(UIElement adorned) : Adorner(adorned)
    {
        private double _y;

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
            var width = ((FrameworkElement)AdornedElement).ActualWidth;
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(2, Math.Max(0, _y - 1), Math.Max(0, width - 4), 3), 1.5, 1.5);
        }
    }
}

/// <summary>Pure drag-and-drop geometry (unit-tested).</summary>
public static class BoardDragDrop
{
    public const string Format = "WorkTrace.BoardCard";

    /// <summary>Index to insert at for a pointer at <paramref name="y"/>: before the first row whose middle is below it.</summary>
    public static int InsertionIndex(IReadOnlyList<(double Top, double Height)> rows, double y)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (y < rows[i].Top + (rows[i].Height / 2))
            {
                return i;
            }
        }

        return rows.Count;
    }
}
