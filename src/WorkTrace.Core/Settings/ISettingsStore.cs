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

    /// <summary>
    /// Applies <paramref name="change"/> to a copy of the latest settings and saves it, serialised with other
    /// updates. Use this rather than Current + SaveAsync when several parts of the app change settings
    /// (e.g. the tracking engine pausing while the settings page is open) so no update is lost.
    /// </summary>
    /// <exception cref="SettingsValidationException">The result is invalid; nothing is written.</exception>
    Task<WorkTraceSettings> UpdateAsync(Action<WorkTraceSettings> change, CancellationToken cancellationToken = default);

    /// <summary>Validates and atomically persists settings.</summary>
    /// <exception cref="SettingsValidationException">The settings are invalid; nothing is written.</exception>
    Task SaveAsync(WorkTraceSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SettingsValidationException(IReadOnlyList<string> errors)
    : Exception("Settings are invalid: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
