using WorkTrace.Core.Management;

namespace WorkTrace.App.ViewModels;

/// <summary>A row in the task list.</summary>
public sealed class TaskItemViewModel(TaskSummary summary, string lastUsedText)
{
    public TaskSummary Summary { get; } = summary;

    public int Id => Summary.Id;

    public string Title => Summary.Title;

    public string? ExternalId => Summary.ExternalId;

    public bool HasExternalId => !string.IsNullOrEmpty(Summary.ExternalId);

    public string Context => Summary.TaskSystemName is { } system
        ? $"{Summary.ClientName} · {system}"
        : Summary.ClientName;

    public bool IsArchived => Summary.IsArchived;

    public string LastUsedText { get; } = lastUsedText;
}
