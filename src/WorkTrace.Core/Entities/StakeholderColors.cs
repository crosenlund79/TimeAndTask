namespace WorkTrace.Core.Entities;

/// <summary>Restrained palette for stakeholders (readable as a tint and as a thin accent in light and dark themes).</summary>
public static class StakeholderColors
{
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        ("Slate blue", "#5B7DB1"), ("Teal", "#4F8A8B"), ("Sage", "#6B8E5A"), ("Ochre", "#B08D57"), ("Terracotta", "#A0675E"),
        ("Plum", "#8A6FA8"), ("Steel", "#5E8C9A"), ("Mauve", "#B07A9E"), ("Olive", "#8F8A55"), ("Graphite", "#7A7F87"),
    ];

    /// <summary>The stored colour, or a stable default per stakeholder (existing stakeholders get one automatically).</summary>
    public static string For(int id, string? stored) =>
        IsValid(stored) ? stored!.ToUpperInvariant() : Palette[(int)((uint)(id - 1) % (uint)Palette.Count)].Hex;

    public static bool IsValid(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && hex[1..].All(Uri.IsHexDigit);
}
