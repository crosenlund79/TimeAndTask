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
        IReadOnlyList<PickerOption> channels)
    {
        Existing = existing;
        _original = existing is null
            ? new TaskInput(defaultClientId, null, null, null, null, null)
            : new TaskInput(existing.ClientId, existing.TaskSystemId, existing.ExternalId, existing.Title, existing.Notes, existing.DefaultChannelId);

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
    }

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

    /// <summary>Text typed into the editable client box; used to offer "create client".</summary>
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

    public string CreateClientLabel => $"Create client “{Text.Clean(ClientText)}”";

    public TaskInput ToInput() => new(ClientId, TaskSystemId, ExternalId, Title, Notes, DefaultChannelId);

    public void ApplyErrors(ValidationErrors errors)
    {
        ClientError = errors.For(nameof(TaskInput.ClientId));
        TitleError = errors.For(nameof(TaskInput.Title));
        ExternalIdError = errors.For(nameof(TaskInput.ExternalId));
        TaskSystemError = errors.For(nameof(TaskInput.TaskSystemId));
        ChannelError = errors.For(nameof(TaskInput.DefaultChannelId));
        NotesError = errors.For(nameof(TaskInput.Notes));
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
        && Text.Clean(a.ExternalId) == Text.Clean(b.ExternalId)
        && Text.Clean(a.Title) == Text.Clean(b.Title)
        && Text.Clean(a.Notes) == Text.Clean(b.Notes);
}
