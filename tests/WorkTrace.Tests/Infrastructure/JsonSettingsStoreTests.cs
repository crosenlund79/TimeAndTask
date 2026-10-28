using Microsoft.Extensions.DependencyInjection;
using WorkTrace.Core.Settings;

namespace WorkTrace.Tests.Infrastructure;

public class JsonSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingFile_CreatesDefaults()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();

        var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync(Ct);

        Assert.Empty(settings.Validate());
        Assert.True(File.Exists(data.Paths.SettingsPath));
    }

    [Fact]
    public async Task SavedSettings_SurviveRestart()
    {
        using var data = new TestDataDirectory();
        await using (var services = data.BuildServices())
        {
            var store = services.GetRequiredService<ISettingsStore>();
            var settings = await store.LoadAsync(Ct);
            settings.Tracking.CheckInIntervalMinutes = 45;
            settings.Tracking.MaxEstimationHorizonMinutes = 90;
            settings.Screenshots.Format = ScreenshotFormat.Jpeg;
            settings.Screenshots.JpegQuality = 70;
            await store.SaveAsync(settings, Ct);
        }

        await using (var services = data.BuildServices())
        {
            var reloaded = await services.GetRequiredService<ISettingsStore>().LoadAsync(Ct);
            Assert.Equal(45, reloaded.Tracking.CheckInIntervalMinutes);
            Assert.Equal(ScreenshotFormat.Jpeg, reloaded.Screenshots.Format);
            Assert.Equal(70, reloaded.Screenshots.JpegQuality);
        }

        // Enums are written as names for readability.
        Assert.Contains("\"Jpeg\"", await File.ReadAllTextAsync(data.Paths.SettingsPath, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidSettings_AreNotSaved()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();
        var store = services.GetRequiredService<ISettingsStore>();
        await store.LoadAsync(Ct);
        var before = await File.ReadAllTextAsync(data.Paths.SettingsPath, Ct);

        var invalid = new WorkTraceSettings();
        invalid.Reporting.RoundingMinutes = 7;

        var ex = await Assert.ThrowsAsync<SettingsValidationException>(() => store.SaveAsync(invalid, Ct));
        Assert.NotEmpty(ex.Errors);
        Assert.Equal(before, await File.ReadAllTextAsync(data.Paths.SettingsPath, Ct));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{ \"Tracking\": { \"CheckInIntervalMinutes\": 0 } }")]
    [InlineData("null")]
    public async Task CorruptOrInvalidFile_IsPreserved_AndDefaultsUsed(string content)
    {
        using var data = new TestDataDirectory();
        data.Paths.EnsureDirectories();
        await File.WriteAllTextAsync(data.Paths.SettingsPath, content, Ct);
        await using var services = data.BuildServices();

        var settings = await services.GetRequiredService<ISettingsStore>().LoadAsync(Ct);

        Assert.Equal(30, settings.Tracking.CheckInIntervalMinutes);
        var preserved = Assert.Single(Directory.GetFiles(data.Paths.DataDirectory, "settings.invalid-*.json"));
        Assert.Equal(content, await File.ReadAllTextAsync(preserved, Ct));
    }

    [Fact]
    public async Task Save_RaisesChanged()
    {
        using var data = new TestDataDirectory();
        await using var services = data.BuildServices();
        var store = services.GetRequiredService<ISettingsStore>();
        var settings = await store.LoadAsync(Ct);
        WorkTraceSettings? received = null;
        store.Changed += (_, s) => received = s;

        await store.SaveAsync(settings, Ct);

        Assert.Same(settings, received);
    }
}
