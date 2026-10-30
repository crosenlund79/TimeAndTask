using System.Reflection;

namespace WorkTrace.App.Shell;

/// <summary>The product version (set at build time: 0.2.&lt;CI run number&gt;; same as the installer's).</summary>
public static class AppVersion
{
    public static string Text { get; } =
        (typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0]; // drop the source-link commit suffix
}
