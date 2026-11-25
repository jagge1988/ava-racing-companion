using System.Text.Json;
using AvaRacingCompanion.Core.Versioning;

namespace AvaRacingCompanion.Core.Config;

/// <summary>
/// Liest und schreibt Benutzereinstellungen atomar als gut lesbare JSON-Datei.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public SettingsStore(string? customFilePath = null)
    {
        if (!string.IsNullOrWhiteSpace(customFilePath))
        {
            FilePath = customFilePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            FilePath = Path.Combine(appData, AppInfo.AppId, "settings.json");
        }
    }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return (settings ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalized();
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(normalized, JsonOptions);

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, FilePath, overwrite: true);
    }
}
