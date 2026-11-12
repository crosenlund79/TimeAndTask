using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using WorkTrace.Core.Settings;

namespace WorkTrace.App.Interop;

/// <summary>
/// Autostart through the current user's Run key (HKCU, so no administrator rights). The value points at the running
/// WorkTrace.exe with <see cref="Argument"/>; it is rewritten at every start while the setting is on, so it stays valid
/// after upgrades or a move. Windows' own "Startup apps" switch (StartupApproved) is left alone.
/// </summary>
internal sealed class RunKeyAutostart(ILogger<RunKeyAutostart> logger) : IAutostart
{
    /// <summary>Passed by the login start: a second WorkTrace started this way exits quietly.</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WorkTrace";

    public bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The WorkTrace executable path is unknown.");
            var command = $"\"{exe}\" {Argument}";
            if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, command, RegistryValueKind.String);
                logger.LogInformation("Registered to start with Windows");
            }
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            logger.LogInformation("No longer starts with Windows");
        }
    }
}
