namespace WorkTrace.App.ViewModels;

/// <summary>
/// An item in a selection list. Database ids are positive; <see cref="NoneKey"/> (0) represents
/// "none"/"all" so that the choice can be selected and displayed by a ComboBox (null cannot).
/// </summary>
public sealed record PickerOption(int Id, string Name, bool IsActive = true)
{
    public const int NoneKey = 0;

    public static int? FromKey(int key) => key == NoneKey ? null : key;

    public string DisplayName => IsActive ? Name : $"{Name} (hidden)";

    public override string ToString() => Name;
}
