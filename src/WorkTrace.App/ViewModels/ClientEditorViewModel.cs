using CommunityToolkit.Mvvm.ComponentModel;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

public sealed partial class ClientEditorViewModel : ObservableObject
{
    private readonly ClientInput _original;

    public ClientEditorViewModel(ClientSummary? existing)
    {
        Existing = existing;
        _original = existing is null
            ? new ClientInput(null, null, false, null)
            : new ClientInput(existing.Name, existing.Description, existing.IsInternal, existing.Color);
        Name = _original.Name;
        Color = _original.Color;
        Description = _original.Description;
        IsInternal = _original.IsInternal;
    }

    public ClientSummary? Existing { get; }

    public bool IsNew => Existing is null;

    public int? Id => Existing?.Id;

    public bool IsActive => Existing?.IsActive ?? true;

    public string Header => IsNew ? "New stakeholder" : Existing!.Name;

    public string? UsageText => Existing switch
    {
        null => null,
        { TaskCount: 0 } => "No tasks yet",
        { TaskCount: 1 } => "1 task",
        var c => c.ActiveTaskCount == c.TaskCount ? $"{c.TaskCount} tasks" : $"{c.TaskCount} tasks ({c.TaskCount - c.ActiveTaskCount} archived)",
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial bool IsInternal { get; set; }

    /// <summary>Muted colour for Board cards ("#RRGGBB"); null for a new stakeholder = a default from the palette.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirty))]
    public partial string? Color { get; set; }

    public IReadOnlyList<ColorOption> ColorOptions { get; } =
        [.. WorkTrace.Core.Entities.StakeholderColors.Palette.Select(p => new ColorOption(p.Name, p.Hex))];

    [ObservableProperty]
    public partial string? NameError { get; private set; }

    [ObservableProperty]
    public partial string? DescriptionError { get; private set; }

    public bool IsDirty =>
        Text.Clean(Name) != Text.Clean(_original.Name)
        || Text.Clean(Description) != Text.Clean(_original.Description)
        || IsInternal != _original.IsInternal
        || !string.Equals(Color, _original.Color, StringComparison.OrdinalIgnoreCase);

    public ClientInput ToInput() => new(Name, Description, IsInternal, Color);

    public void ApplyErrors(ValidationErrors errors)
    {
        NameError = errors.For(nameof(ClientInput.Name));
        DescriptionError = errors.For(nameof(ClientInput.Description));
    }

    partial void OnNameChanged(string? value) => NameError = null;

    partial void OnDescriptionChanged(string? value) => DescriptionError = null;
}

public sealed record ColorOption(string Name, string Hex);
