using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WorkTrace.App.ViewModels;

namespace WorkTrace.App.Views;

/// <summary>
/// Window behaviour only: sizes the two timesheet sections from the saved layout (a share of the height, or the docked
/// weekly hours' height) and reports the divider position back when it has been dragged.
/// </summary>
public partial class TimesheetView : UserControl
{
    private TimesheetViewModel? _viewModel;

    public TimesheetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as TimesheetViewModel);
        Loaded += (_, _) => ApplyLayout();
        Sections.SizeChanged += (_, _) => KeepDockedHoursInside();
    }

    private double Available => Math.Max(0, Sections.ActualHeight - Divider.ActualHeight);

    private void Attach(TimesheetViewModel? viewModel)
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

        ApplyLayout();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TimesheetViewModel.DockHoursToBottom))
        {
            ApplyLayout();
        }
    }

    /// <summary>Not docked: both sections scale with the window by the saved share. Docked: the weekly hours keep their height.</summary>
    private void ApplyLayout()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.DockHoursToBottom)
        {
            var height = _viewModel.DockedHoursHeight > 0 ? _viewModel.DockedHoursHeight : Available / 2;
            TasksRow.Height = new GridLength(1, GridUnitType.Star);
            HoursRow.Height = height > 0 ? new GridLength(height) : new GridLength(1, GridUnitType.Star);
            KeepDockedHoursInside();
        }
        else
        {
            var share = _viewModel.TasksShare;
            TasksRow.Height = new GridLength(share, GridUnitType.Star);
            HoursRow.Height = new GridLength(1 - share, GridUnitType.Star);
        }
    }

    /// <summary>A docked height larger than a smaller window allows is shown smaller (the saved value is kept).</summary>
    private void KeepDockedHoursInside()
    {
        if (_viewModel is not { DockHoursToBottom: true } || !HoursRow.Height.IsAbsolute || Available <= 0)
        {
            return;
        }

        var wanted = _viewModel.DockedHoursHeight > 0 ? _viewModel.DockedHoursHeight : Available / 2;
        var fits = Math.Max(HoursRow.MinHeight, Math.Min(wanted, Available - TasksRow.MinHeight));
        if (Math.Abs(HoursRow.Height.Value - fits) > 0.5)
        {
            HoursRow.Height = new GridLength(fits);
        }
    }

    private void OnDividerDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var tasks = TasksRow.ActualHeight;
        var hours = HoursRow.ActualHeight;
        if (_viewModel.DockHoursToBottom)
        {
            _ = _viewModel.SaveDockedHoursHeightAsync(hours);
            TasksRow.Height = new GridLength(1, GridUnitType.Star);
            HoursRow.Height = new GridLength(hours); // stays docked: the hours keep this height when the window changes
        }
        else if (tasks + hours > 0)
        {
            _ = _viewModel.SaveTasksShareAsync(tasks / (tasks + hours));
        }
    }
}
