using System.Text.Json;
using AvaRacingCompanion.Core.Versioning;

namespace AvaRacingCompanion.Telemetry.Api;

public sealed record OutboxItem(
    string EndpointPath,
    string JsonPayload,
    bool Gzip,
    DateTimeOffset CreatedAtUtc);

public sealed record OutboxFlushResult(
    int SentCount,
    bool AuthRevoked);

/// <summary>
/// Persistente Offline-Queue (%APPDATA%\AvaRacingCompanion\outbox\), damit bei
/// kurzzeitigem Verbindungsabbruch keine Runden-, Pitstop- oder Trace-Daten verloren gehen.
/// </summary>
public sealed class OfflineUploadOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _directoryPath;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private int _sequence;

    public OfflineUploadOutbox(string? customDirectoryPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customDirectoryPath))
        {
            _directoryPath = customDirectoryPath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _directoryPath = Path.Combine(appData, AppInfo.AppId, "outbox");
        }
    }

    public int PendingCount
    {
        get
        {
            if (!Directory.Exists(_directoryPath))
            {
                return 0;
            }

            return Directory.GetFiles(_directoryPath, "*.json").Length;
        }
    }

    public void Enqueue<TPayload>(string endpointPath, TPayload payload, bool gzip = false)
    {
        Directory.CreateDirectory(_directoryPath);

        var jsonPayload = JsonSerializer.Serialize(payload);
        var item = new OutboxItem(endpointPath, jsonPayload, gzip, DateTimeOffset.UtcNow);
        var seq = Interlocked.Increment(ref _sequence) % 10000;
        var fileName = $"{DateTimeOffset.UtcNow.UtcTicks:D20}_{seq:D4}.json";
        var targetPath = Path.Combine(_directoryPath, fileName);
        var tempPath = $"{targetPath}.tmp";

        File.WriteAllText(tempPath, JsonSerializer.Serialize(item, JsonOptions));
        File.Move(tempPath, targetPath, overwrite: true);
    }

    public async Task<OutboxFlushResult> FlushAsync(
        TelemetryApiClient client,
        string serverBaseUrl,
        string? deviceToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverBaseUrl) || string.IsNullOrWhiteSpace(deviceToken))
        {
            return new OutboxFlushResult(0, false);
        }

        if (!Directory.Exists(_directoryPath))
        {
            return new OutboxFlushResult(0, false);
        }

        await _flushLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var files = Directory.GetFiles(_directoryPath, "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToArray();

            var sentCount = 0;
            var authRevoked = false;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                OutboxItem? item;
                try
                {
                    item = JsonSerializer.Deserialize<OutboxItem>(File.ReadAllText(file), JsonOptions);
                }
                catch
                {
                    SafeDelete(file);
                    continue;
                }

                if (item is null)
                {
                    SafeDelete(file);
                    continue;
                }

                var result = await client.PostRawJsonAsync(
                    serverBaseUrl,
                    deviceToken,
                    item.EndpointPath,
                    item.JsonPayload,
                    item.Gzip,
                    cancellationToken).ConfigureAwait(false);

                if (result.Success)
                {
                    SafeDelete(file);
                    sentCount++;
                }
                else if (result.IsAuthError)
                {
                    // Token ungültig/widerrufen (z. B. /unpair): Dateien behalten, UI informieren
                    authRevoked = true;
                    break;
                }
                else
                {
                    // Netzwerk/Server temporär nicht erreichbar: FIFO beibehalten und später erneut versuchen
                    break;
                }
            }

            return new OutboxFlushResult(sentCount, authRevoked);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignorieren
        }
    }
}
