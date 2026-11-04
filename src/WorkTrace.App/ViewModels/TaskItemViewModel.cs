using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>A row in the task list.</summary>
public sealed class TaskItemViewModel(TaskSummary summary, string lastUsedText, TaskGroupViewModel group)
{
    /// <summary>The list group the row is shown in (the list groups by this object).</summary>
    public TaskGroupViewModel Group { get; } = group;

    public TaskSummary Summary { get; } = summary;

    public int Id => Summary.Id;

    public string Title => Summary.Title;

    public string? ExternalId => Summary.ExternalId;

    public bool HasExternalId => !string.IsNullOrEmpty(Summary.ExternalId);

    public string Context => Summary.TaskSystemName is { } system
        ? $"{Summary.ClientName} · {system}"
        : Summary.ClientName;

    public bool IsArchived => Summary.IsArchived;

    public bool HasDeadlineWarning => Summary.DeadlineState != WorkTrace.Core.Management.DeadlineState.None;

    public string? DeadlineText => CardText.DeadlineText(Summary.Deadline, Summary.DeadlineState);

    public string LastUsedText { get; } = lastUsedText;

    public bool IsWaiting => Summary.IsWaiting;

    public string? WaitingReason => Summary.WaitingReason;

    public string WaitingText => string.IsNullOrWhiteSpace(Summary.WaitingReason) ? "Waiting for external input" : "Waiting for: " + Summary.WaitingReason;
}
