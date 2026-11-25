using AvaRacingCompanion.Core.Config;
using Xunit;

namespace AvaRacingCompanion.Core.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public SettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ava-companion-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
    }

    [Fact]
    public void Load_WhenFileDoesNotExist_ReturnsDefaults()
    {
        var store = new SettingsStore(_settingsPath);

        var settings = store.Load();

        Assert.Equal(UpdateBehavior.AutoUpdate, settings.UpdateBehavior);
        Assert.False(settings.LaunchAtStartup);
        Assert.Equal(AppSettings.DefaultUpdateCheckIntervalMinutes, settings.UpdateCheckIntervalMinutes);
        Assert.Null(settings.LastNotifiedVersion);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNotifyOnlySettings()
    {
        var store = new SettingsStore(_settingsPath);
        var expected = new AppSettings
        {
            UpdateBehavior = UpdateBehavior.NotifyOnly,
            LaunchAtStartup = true,
            UpdateCheckIntervalMinutes = 120,
            LastNotifiedVersion = "1.2.3"
        };

        store.Save(expected);
        var loaded = store.Load();

        Assert.Equal(expected, loaded);

        var rawJson = File.ReadAllText(_settingsPath);
        Assert.Contains("\"NotifyOnly\"", rawJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_ClampsOutOfRangeUpdateInterval()
    {
        var store = new SettingsStore(_settingsPath);
        store.Save(new AppSettings { UpdateCheckIntervalMinutes = 1 });

        var loaded = store.Load();

        Assert.Equal(AppSettings.MinUpdateCheckIntervalMinutes, loaded.UpdateCheckIntervalMinutes);
    }

    [Fact]
    public void Load_WhenJsonIsCorrupted_FallsBackToDefaults()
    {
        File.WriteAllText(_settingsPath, "{ not valid json");
        var store = new SettingsStore(_settingsPath);

        var loaded = store.Load();

        Assert.Equal(UpdateBehavior.AutoUpdate, loaded.UpdateBehavior);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
