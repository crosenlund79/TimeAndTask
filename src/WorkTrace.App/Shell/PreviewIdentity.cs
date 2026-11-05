using System.IO;
using System.Reflection;

namespace WorkTrace.App.Shell;

/// <summary>
/// Visual-preview builds (<c>-p:WorkTraceVariant=ModernFluent</c> etc.): a separate application identity with its own
/// data folder, single-instance names, credentials and installer, so a preview never touches the real WorkTrace.
/// A normal build has no variant and keeps every production name unchanged.
/// </summary>
internal static class PreviewIdentity
{
    /// <summary>The build-time theme variant, or null for the production WorkTrace.</summary>
    public static string? Variant { get; } = typeof(PreviewIdentity).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "WorkTraceVariant")?.Value is { Length: > 0 } v ? v : null;

    public static bool IsPreview => Variant is not null;

    public static string ThemeName => Variant switch
    {
        "ModernFluent" => "Modern Fluent",
        "DarkProfessional" => "Dark Professional",
        "RefinedNeutral" => "Refined Neutral",
        "ModernGradient" => "Modern Gradient",
        "GlassDepth" => "Glass & Depth",
        null => "",
        var other => other,
    };

    /// <summary>"WorkTrace", or "WT - Modern Fluent" for a preview.</summary>
    public static string DisplayName => IsPreview ? $"WT - {ThemeName}" : "WorkTrace";

    /// <summary>Appended to the single-instance mutex/event names (empty for production: names never change).</summary>
    public static string InstanceSuffix => IsPreview ? "." + Variant : "";

    /// <summary>Prefix for Windows Credential Manager entries of a preview (empty for production).</summary>
    public static string SecretPrefix => IsPreview ? $"WT-Preview-{Variant}/" : "";

    /// <summary>
    /// A preview's own data folder, <c>%LocalAppData%\WT-Preview\{Variant}</c>. WORKTRACE_DATA_DIR is ignored on
    /// purpose, so a preview can never open the real database. Null for production.
    /// </summary>
    public static string? DataDirectory => IsPreview
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "WT-Preview", Variant!)
        : null;

    public static string PreviewBadge => $"VISUAL PREVIEW · {ThemeName}";
}
