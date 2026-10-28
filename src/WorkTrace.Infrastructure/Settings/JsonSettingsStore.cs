using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WorkTrace.Core.Settings;
using WorkTrace.Infrastructure.Storage;

namespace WorkTrace.Infrastructure.Settings;

/// <summary>
/// Stores <see cref="WorkTraceSettings"/> as indented JSON. Writes go to a temporary file which then
/// atomically replaces the target, so a crash mid-write cannot leave a truncated settings file.
/// </summary>
public sealed class JsonSettingsStore(AppPaths paths, TimeProvider timeProvider, ILogger<JsonSettingsStore> logger) : ISettingsStore, IDisposable
{
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkTraceSettings Current { get; private set; } = new();

    public event EventHandler<WorkTraceSettings>? Changed;

    public async Task<WorkTraceSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Current = await ReadOrDefaultAsync(cancellationToken);
            return Current;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(WorkTraceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new SettingsValidationException(errors);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await WriteAsync(settings, cancellationToken);
            Current = settings;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, settings);
    }

    public async Task<WorkTraceSettings> UpdateAsync(Action<WorkTraceSettings> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        WorkTraceSettings updated;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            updated = Current.Clone();
            change(updated);
            var errors = updated.Validate();
            if (errors.Count > 0)
            {
                throw new SettingsValidationException(errors);
            }

            await WriteAsync(updated, cancellationToken);
            Current = updated;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, updated);
        return updated;
    }

    public void Dispose() => _gate.Dispose();

    private async Task<WorkTraceSettings> ReadOrDefaultAsync(CancellationToken cancellationToken)
    {
        var path = paths.SettingsPath;
        if (!File.Exists(path))
        {
            var defaults = new WorkTraceSettings();
            await WriteAsync(defaults, cancellationToken);
            logger.LogInformation("Created default settings file");
            return defaults;
        }

        string? problem;
        try
        {
            await using var stream = File.OpenRead(path);
            var loaded = await JsonSerializer.DeserializeAsync<WorkTraceSettings>(stream, SerializerOptions, cancellationToken);
            if (loaded is null)
            {
                problem = "file is empty";
            }
            else
            {
                var errors = loaded.Validate();
                if (errors.Count == 0)
                {
                    return loaded;
                }

                problem = string.Join(" ", errors);
            }
        }
        catch (JsonException ex)
        {
            problem = "invalid JSON: " + ex.Message;
        }

        // Keep the broken file for the user to inspect, then continue with defaults.
        var stamp = timeProvider.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var preserved = Path.Combine(paths.DataDirectory, $"settings.invalid-{stamp}.json");
        File.Move(path, preserved, overwrite: true);
        logger.LogWarning("Settings file was invalid ({Problem}); preserved as {File} and reverted to defaults", problem, Path.GetFileName(preserved));

        var fallback = new WorkTraceSettings();
        await WriteAsync(fallback, cancellationToken);
        return fallback;
    }

    private async Task WriteAsync(WorkTraceSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temp = paths.SettingsPath + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(temp, paths.SettingsPath, overwrite: true);
    }
}
