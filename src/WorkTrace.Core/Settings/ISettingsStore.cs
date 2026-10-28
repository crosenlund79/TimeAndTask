namespace WorkTrace.Core.Settings;

public interface ISettingsStore
{
    /// <summary>The most recently loaded or saved settings.</summary>
    WorkTraceSettings Current { get; }

    event EventHandler<WorkTraceSettings>? Changed;

    /// <summary>
    /// Loads settings from storage. Missing, unreadable or invalid files fall back to defaults;
    /// an invalid file is preserved for inspection rather than overwritten in place.
    /// </summary>
    Task<WorkTraceSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates and atomically persists settings.</summary>
    /// <exception cref="SettingsValidationException">The settings are invalid; nothing is written.</exception>
    Task SaveAsync(WorkTraceSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsValidationException(IReadOnlyList<string> errors)
    : Exception("Settings are invalid: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
