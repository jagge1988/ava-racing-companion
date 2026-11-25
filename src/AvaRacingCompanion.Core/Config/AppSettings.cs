using System.Text.Json.Serialization;

namespace AvaRacingCompanion.Core.Config;

/// <summary>
/// Legt fest, wie die App auf verfügbare GitHub-Releases reagiert.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdateBehavior
{
    /// <summary>
    /// Lädt Updates automatisch herunter und installiert sie beim Neustart
    /// bzw. per Klick auf „Jetzt aktualisieren“.
    /// </summary>
    AutoUpdate = 0,

    /// <summary>
    /// Lädt nichts ungefragt herunter, sondern zeigt nur eine dezente
    /// Benachrichtigung mit Versionsnummer und Link zum GitHub-Release.
    /// </summary>
    NotifyOnly = 1
}

/// <summary>
/// Lokal gespeicherte Benutzereinstellungen (%APPDATA%\AvaRacingCompanion\settings.json).
/// </summary>
public sealed record AppSettings
{
    public const int MinUpdateCheckIntervalMinutes = 15;
    public const int MaxUpdateCheckIntervalMinutes = 1440;
    public const int DefaultUpdateCheckIntervalMinutes = 60;

    public UpdateBehavior UpdateBehavior { get; init; } = UpdateBehavior.AutoUpdate;

    public bool LaunchAtStartup { get; init; }

    public int UpdateCheckIntervalMinutes { get; init; } = DefaultUpdateCheckIntervalMinutes;

    /// <summary>
    /// Merkt sich im Modus „Nur benachrichtigen“ die zuletzt gemeldete Version,
    /// um wiederholte Benachrichtigungen für dasselbe Release zu vermeiden.
    /// </summary>
    public string? LastNotifiedVersion { get; init; }

    public AppSettings Normalized()
    {
        var clampedInterval = Math.Clamp(
            UpdateCheckIntervalMinutes,
            MinUpdateCheckIntervalMinutes,
            MaxUpdateCheckIntervalMinutes);

        var behavior = Enum.IsDefined(UpdateBehavior)
            ? UpdateBehavior
            : UpdateBehavior.AutoUpdate;

        return this with
        {
            UpdateBehavior = behavior,
            UpdateCheckIntervalMinutes = clampedInterval
        };
    }
}
