using CommunityToolkit.Mvvm.ComponentModel;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>
/// Edit form for one task. Holds a snapshot of the original values to compute <see cref="IsDirty"/>;
/// saving is performed by <see cref="TasksViewModel"/>.
/// </summary>
public sealed partial class TaskEditorViewModel : ObservableObject
{
    private readonly TaskInput _original;

    public TaskEditorViewModel(
        TaskSummary? existing,
        int? defaultClientId,
        IReadOnlyList<PickerOption> clients,
        IReadOnlyList<PickerOption> taskSystems,
        IReadOnlyList<PickerOption> channels,
        bool showNewTasksOnBoard = true)
    {
        Existing = existing;
        _original = existing is null
            ? new TaskInput(defaultClientId, null, null, null, null, null, showNewTasksOnBoard, null, false, false, null, false)
            : new TaskInput(existing.ClientId, existing.TaskSystemId, existing.ExternalId, existing.Title, existing.Notes, existing.DefaultChannelId, existing.IsOnBoard, existing.Deadline,
                existing.IsRecurring, existing.IsWaiting, existing.WaitingReason, existing.IsClosed, existing.EstimatedRemainingMinutes, existing.PlannedStart);
        IsClosed = _original.IsClosed == true;
        EstimateText = _original.EstimatedRemainingMinutes is { } est ? FormatEstimate(est) : null;
        PlannedStart = _original.PlannedStart;
        Deadline = _original.Deadline;
        IsRecurring = _original.IsRecurring == true;
        IsWaiting = _original.IsWaiting == true;
        WaitingReason = _original.WaitingReason;
        ShowOnBoard = _original.ShowOnBoard ?? true;

        ClientId = _original.ClientId;
        TaskSystemId = _original.TaskSystemId;
        ExternalId = _original.ExternalId;
        Title = _original.Title;
        Notes = _original.Notes;
        DefaultChannelId = _original.DefaultChannelId;

        // Hidden/inactive entries stay selectable only when this task already uses them.
        ClientOptions = [.. clients.Where(c => c.IsActive || c.Id == ClientId)];
        TaskSystemOptions = [new PickerOption(PickerOption.NoneKey, "None"), .. taskSystems.Where(s => s.IsActive || s.Id == TaskSystemId)];
        ChannelOptions = [new PickerOption(PickerOption.NoneKey, "None"), .. channels.Where(c => c.IsActive || c.Id == DefaultChannelId)];

        // The editable client box binds both SelectedValue and Text. Its text must start as the selected
        // client's name: an empty text would make the ComboBox's type-ahead clear the selection on load.
        ClientText = ClientOptions.FirstOrDefault(o => o.Id == ClientId)?.Name;
    }

    /// <summary>The values the editor was opened with (for diagnostics and tests).</summary>
    public TaskInput Original => _original;

    public TaskSummary? Existing { get; }

    public bool IsNew => Existing is null;

    public int? Id => Existing?.Id;

    public bool IsArchived => Existing?.IsArchived ?? false;

    public string Header => IsNew ? "New task" : Existing!.Title;

    public IReadOnlyList<PickerOption> ClientOptions { get; }

    public IReadOnlyList<PickerOption> TaskSystemOptions { get; }

    public IReadOnlyList<PickerOption> ChannelOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(CanCreateClient), nameof(CreateClientLabel))]
    public partial int? ClientId { get; set; }

    /// <summary>Text typed into the editable client box; used to offer "create stakeholder".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreateClient), nameof(CreateClientLabel))]
    public partial string? ClientText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial int? TaskSystemId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? ExternalId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? Title { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? Notes { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial int? DefaultChannelId { get; set; }

    /// <summary>The task has a card on the Board (off = card archived; the task and its time are untouched).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool ShowOnBoard { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(DeadlineDate))]
    public partial DateOnly? Deadline { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool IsRecurring { get; set; }

    /// <summary>Waiting for external input (information, a response, approval or action from others).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool IsWaiting { get; set; }

    /// <summary>"Waiting for / Reason" (optional, plain text). Cleared when the waiting status is cleared.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? WaitingReason { get; set; }

    [ObservableProperty]
    public partial string? WaitingReasonError { get; private set; }

    /// <summary>Completed (reopen by clearing it). Different from archiving, which hides the task.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool IsClosed { get; set; }

    /// <summary>Estimated remaining time as typed ("2:30", "1.5", "3h", "45m"); empty = unknown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? EstimateText { get; set; }

    [ObservableProperty]
    public partial string? EstimateError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty), nameof(PlannedStartDate))]
    public partial DateOnly? PlannedStart { get; set; }

    public DateTime? PlannedStartDate
    {
        get => PlannedStart?.ToDateTime(TimeOnly.MinValue);
        set => PlannedStart = value is { } d ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>Minutes, null for empty text; -1 for text that is not a duration (rejected by validation).</summary>
    public int? EstimatedMinutes => ParseEstimate(EstimateText);

    public static string FormatEstimate(int minutes) => $"{minutes / 60}:{minutes % 60:00}";

    public static int? ParseEstimate(string? text)
    {
        var t = text?.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal);
        if (string.IsNullOrEmpty(t))
        {
            return null;
        }

        if (t.EndsWith('h'))
        {
            t = t[..^1];
        }

        if (t.Contains(':', StringComparison.Ordinal) || t.EndsWith('m'))
        {
            return WorkTrace.Core.Reporting.Hours.ParseMinutes(t) ?? -1;
        }

        return decimal.TryParse(t.Replace(',', '.'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var hours)
            && hours is >= 0 and <= 1000
            ? (int)Math.Round(hours * 60)
            : -1;
    }

    partial void OnEstimateTextChanged(string? value) => EstimateError = null;

    /// <summary>DatePicker-friendly <see cref="Deadline"/>.</summary>
    public DateTime? DeadlineDate
    {
        get => Deadline?.ToDateTime(TimeOnly.MinValue);
        set => Deadline = value is { } d ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>
    /// ComboBox-friendly key for <see cref="TaskSystemId"/> (0 = none). A null write is a transient
    /// ComboBox state (items being replaced), never a user choice, and is ignored.
    /// </summary>
    public int? TaskSystemKey
    {
        get => TaskSystemId ?? PickerOption.NoneKey;
        set
        {
            if (value is { } key)
            {
                TaskSystemId = PickerOption.FromKey(key);
            }
        }
    }

    /// <summary>ComboBox-friendly key for <see cref="DefaultChannelId"/> (0 = none); null writes are ignored.</summary>
    public int? ChannelKey
    {
        get => DefaultChannelId ?? PickerOption.NoneKey;
        set
        {
            if (value is { } key)
            {
                DefaultChannelId = PickerOption.FromKey(key);
            }
        }
    }

    [ObservableProperty]
    public partial string? ClientError { get; private set; }

    [ObservableProperty]
    public partial string? TitleError { get; private set; }

    [ObservableProperty]
    public partial string? ExternalIdError { get; private set; }

    [ObservableProperty]
    public partial string? TaskSystemError { get; private set; }

    [ObservableProperty]
    public partial string? ChannelError { get; private set; }

    [ObservableProperty]
    public partial string? NotesError { get; private set; }

    /// <summary>Non-blocking hint about other tasks with the same external ID.</summary>
    [ObservableProperty]
    public partial string? SimilarWarning { get; set; }

    public bool IsDirty => !Equivalent(ToInput(), _original);

    public bool CanCreateClient =>
        ClientId is null
        && Text.Clean(ClientText) is { } name
        && !ClientOptions.Any(o => Text.SameName(o.Name, name));

    public string CreateClientLabel => $"Create stakeholder “{Text.Clean(ClientText)}”";

    public TaskInput ToInput() => new(ClientId, TaskSystemId, ExternalId, Title, Notes, DefaultChannelId, ShowOnBoard, Deadline,
        IsRecurring, IsWaiting, IsWaiting ? WaitingReason : null, IsClosed, EstimatedMinutes, PlannedStart);

    public void ApplyErrors(ValidationErrors errors)
    {
        ClientError = errors.For(nameof(TaskInput.ClientId));
        TitleError = errors.For(nameof(TaskInput.Title));
        ExternalIdError = errors.For(nameof(TaskInput.ExternalId));
        TaskSystemError = errors.For(nameof(TaskInput.TaskSystemId));
        ChannelError = errors.For(nameof(TaskInput.DefaultChannelId));
        NotesError = errors.For(nameof(TaskInput.Notes));
        WaitingReasonError = errors.For(nameof(TaskInput.WaitingReason));
        EstimateError = errors.For(nameof(TaskInput.EstimatedRemainingMinutes));
    }

    partial void OnClientIdChanged(int? value) => ClientError = null;

    partial void OnTitleChanged(string? value) => TitleError = null;

    partial void OnExternalIdChanged(string? value) => ExternalIdError = null;

    partial void OnTaskSystemIdChanged(int? value)
    {
        TaskSystemError = null;
        OnPropertyChanged(nameof(TaskSystemKey));
    }

    partial void OnDefaultChannelIdChanged(int? value)
    {
        ChannelError = null;
        OnPropertyChanged(nameof(ChannelKey));
    }

    partial void OnNotesChanged(string? value) => NotesError = null;

    private static bool Equivalent(TaskInput a, TaskInput b) =>
        a.ClientId == b.ClientId
        && a.TaskSystemId == b.TaskSystemId
        && a.DefaultChannelId == b.DefaultChannelId
        && a.ShowOnBoard == b.ShowOnBoard
        && a.Deadline == b.Deadline
        && a.IsRecurring == b.IsRecurring
        && a.IsWaiting == b.IsWaiting
        && a.IsClosed == b.IsClosed
        && a.EstimatedRemainingMinutes == b.EstimatedRemainingMinutes
        && a.PlannedStart == b.PlannedStart
        && Text.Clean(a.WaitingReason) == Text.Clean(b.WaitingReason)
        && Text.Clean(a.ExternalId) == Text.Clean(b.ExternalId)
        && Text.Clean(a.Title) == Text.Clean(b.Title)
        && Text.Clean(a.Notes) == Text.Clean(b.Notes);
}
