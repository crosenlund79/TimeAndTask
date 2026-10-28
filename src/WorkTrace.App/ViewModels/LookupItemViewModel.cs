using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WorkTrace.Core.Management;
using WorkTrace.Core.Validation;

namespace WorkTrace.App.ViewModels;

/// <summary>A row in a configurable list. The name is edited inline and committed on Enter or focus loss.</summary>
public sealed partial class LookupItemViewModel : ObservableObject
{
    private readonly LookupListViewModel _owner;
    private readonly LookupSummary _summary;
    private bool _commitStarted;

    public LookupItemViewModel(LookupListViewModel owner, LookupSummary summary, bool isFirst, bool isLast)
    {
        _owner = owner;
        _summary = summary;
        IsFirst = isFirst;
        IsLast = isLast;
        EditName = summary.Name;
    }

    public int Id => _summary.Id;

    public string Name => _summary.Name;

    public bool IsActive => _summary.IsActive;

    public bool IsBuiltIn => _summary.IsBuiltIn;

    public bool IsFirst { get; }

    public bool IsLast { get; }

    public bool CanDelete => !_summary.IsBuiltIn && _summary.UsageCount == 0;

    public string MoveUpToolTip => IsFirst ? "Already at the top" : "Move up";

    public string MoveDownToolTip => IsLast ? "Already at the bottom" : "Move down";

    public string ToggleActiveLabel => IsActive ? "Hide" : "Show";

    public string ToggleActiveGlyph => IsActive ? "\uED1A" : "\uE7B3";

    public string ToggleActiveToolTip => IsActive
        ? "Hide from pickers. Existing tasks and records keep it."
        : "Hidden from pickers. Click to show it again.";

    public string UsageText => _summary.UsageCount switch
    {
        0 => _summary.IsBuiltIn ? "Built in" : "Not used",
        1 => _summary.IsBuiltIn ? "Built in · used once" : "Used once",
        var n => _summary.IsBuiltIn ? $"Built in · used {n} times" : $"Used {n} times",
    };

    public string DeleteToolTip => _summary.IsBuiltIn
        ? "Built-in items can't be deleted — rename or hide them instead"
        : _summary.UsageCount > 0 ? "In use, so it can't be deleted — hide it instead" : "Delete permanently";

    [ObservableProperty]
    public partial string? EditName { get; set; }

    [ObservableProperty]
    public partial string? RenameError { get; private set; }

    partial void OnEditNameChanged(string? value) => RenameError = null;

    internal void ShowRenameError(string? message)
    {
        RenameError = message;
        _commitStarted = false;
    }

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (Text.Clean(EditName) == _summary.Name)
        {
            EditName = _summary.Name;
            return;
        }

        // Enter commits and reloads the list; the TextBox then loses focus and would commit again.
        if (_commitStarted)
        {
            return;
        }

        _commitStarted = true;
        await _owner.RenameAsync(this, EditName);
    }

    [RelayCommand]
    private void CancelRename()
    {
        EditName = _summary.Name;
        RenameError = null;
    }

    [RelayCommand]
    private Task ToggleActiveAsync() => _owner.SetActiveAsync(this, !IsActive);

    [RelayCommand]
    private Task MoveUpAsync() => _owner.MoveAsync(this, -1);

    [RelayCommand]
    private Task MoveDownAsync() => _owner.MoveAsync(this, +1);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}
