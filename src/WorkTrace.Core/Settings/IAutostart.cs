namespace WorkTrace.Core.Settings;

/// <summary>
/// "Start WorkTrace with Windows" for the current user (no administrator rights). The setting
/// (<see cref="GeneralSettings.StartWithWindows"/>) is the source of truth; this applies it to Windows.
/// </summary>
public interface IAutostart
{
    /// <summary>Whether WorkTrace is registered to start at login.</summary>
    bool IsRegistered { get; }

    /// <summary>Registers this WorkTrace executable to start at login, or removes the registration. Idempotent.</summary>
    void Apply(bool enabled);
}
