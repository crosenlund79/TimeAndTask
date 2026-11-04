using CommunityToolkit.Mvvm.ComponentModel;
using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>A collapsible group of the task list. The expanded state is remembered in settings.</summary>
public sealed partial class TaskGroupViewModel(TaskGroup group, string title, string glyph, Action<TaskGroupViewModel> expandedChanged) : ObservableObject
{
    public TaskGroup Group { get; } = group;

    public string Title { get; } = title;

    public string Glyph { get; } = glyph;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    partial void OnIsExpandedChanged(bool value) => expandedChanged(this);

    public override string ToString() => Title;
}
