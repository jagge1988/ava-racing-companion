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
/// Enthält keine globalen Repository-Secrets; ein eventuelles DeviceToken gehört
/// ausschließlich zum lokalen Benutzer nach /pair.
/// </summary>
public sealed record AppSettings
{
    public const int MinUpdateCheckIntervalMinutes = 15;
    public const int MaxUpdateCheckIntervalMinutes = 1440;
    public const int DefaultUpdateCheckIntervalMinutes = 60;
    public const string DefaultServerBaseUrl = "https://ava-racing-bot.tailed88f1.ts.net";

    public UpdateBehavior UpdateBehavior { get; init; } = UpdateBehavior.AutoUpdate;

    public bool LaunchAtStartup { get; init; }

    public int UpdateCheckIntervalMinutes { get; init; } = DefaultUpdateCheckIntervalMinutes;

    /// <summary>
    /// Merkt sich im Modus „Nur benachrichtigen“ die zuletzt gemeldete Version,
    /// um wiederholte Benachrichtigungen für dasselbe Release zu vermeiden.
    /// </summary>
    public string? LastNotifiedVersion { get; init; }

    /// <summary>
    /// Basis-URL der Bot-Ingest-API (z. B. https://ava-racing-bot.tailed88f1.ts.net).
    /// </summary>
    public string ServerBaseUrl { get; init; } = DefaultServerBaseUrl;

    /// <summary>
    /// Persönliches Bearer-Token nach erfolgreichem /pair.
    /// </summary>
    public string? DeviceToken { get; init; }

    public string? PairedDriverName { get; init; }

    public int? PairedCustId { get; init; }

    public bool TelemetryEnabled { get; init; } = true;

    public bool UploadTracesEnabled { get; init; } = true;

    [JsonIgnore]
    public bool IsPaired => !string.IsNullOrWhiteSpace(DeviceToken);

    public AppSettings Normalized()
    {
        var clampedInterval = Math.Clamp(
            UpdateCheckIntervalMinutes,
            MinUpdateCheckIntervalMinutes,
            MaxUpdateCheckIntervalMinutes);

        var behavior = Enum.IsDefined(UpdateBehavior)
            ? UpdateBehavior
            : UpdateBehavior.AutoUpdate;

        var normalizedUrl = (ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');

        return this with
        {
            UpdateBehavior = behavior,
            UpdateCheckIntervalMinutes = clampedInterval,
            ServerBaseUrl = normalizedUrl
        };
    }
}
