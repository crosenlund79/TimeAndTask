using System.Globalization;

namespace WorkTrace.Core.Validation;

/// <summary>Normalization and comparison helpers for user-entered text.</summary>
public static class Text
{
    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions MatchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    /// <summary>Trims; returns null for null/whitespace. Inner text (incl. leading zeros, case) is preserved.</summary>
    public static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>
    /// Case- and accent-insensitive equality for names. Unlike SQLite's NOCASE this also covers
    /// non-ASCII letters (Æ/æ, Ø/ø, Å/å, É/é).
    /// </summary>
    public static bool SameName(string? a, string? b) =>
        Compare.Compare(a?.Trim(), b?.Trim(), MatchOptions) == 0;

    /// <summary>Index of <paramref name="value"/> in <paramref name="source"/>, ignoring case and accents; -1 if absent.</summary>
    public static int IndexOf(string? source, string value) =>
        string.IsNullOrEmpty(source) ? -1 : Compare.IndexOf(source, value, MatchOptions);

    public static bool StartsWith(string? source, string value) =>
        !string.IsNullOrEmpty(source) && Compare.IsPrefix(source, value, MatchOptions);
}
