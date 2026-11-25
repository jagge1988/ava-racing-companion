using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Telemetry.Api;
using AvaRacingCompanion.Telemetry.Sdk;
using AvaRacingCompanion.Telemetry.Tracking;

namespace AvaRacingCompanion.Telemetry;

public sealed record TelemetryRuntimeStatus(
    bool IsConnectedToIRacing,
    string StatusText,
    int PendingUploads);

/// <summary>
/// Hintergrund-Koordinator: Verbindet den 60-Hz-iRacing-SDK-Reader mit dem
/// Session-Tracker, der Offline-Outbox und der Bot-Ingest-API.
/// </summary>
public sealed class TelemetryCoordinator : IDisposable
{
    private readonly IRacingMemoryMappedReader _sdkReader = new();
    private readonly TelemetrySessionTracker _tracker = new();
    private readonly OfflineUploadOutbox _outbox;
    private readonly TelemetryApiClient _apiClient;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private AppSettings _settings = new();
    private string _lastStatusText = "Warte auf iRacing...";
    private bool _lastConnected;

    public event Action<TelemetryRuntimeStatus>? StatusChanged;

    public TelemetryCoordinator(
        OfflineUploadOutbox? outbox = null,
        TelemetryApiClient? apiClient = null)
    {
        _outbox = outbox ?? new OfflineUploadOutbox();
        _apiClient = apiClient ?? new TelemetryApiClient();
    }

    public int PendingUploads => _outbox.PendingCount;

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings.Normalized();
        _tracker.UploadTracesEnabled = _settings.UploadTracesEnabled;
    }

    public void Start(AppSettings initialSettings)
    {
        UpdateSettings(initialSettings);
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        _cts.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normales Beenden
            }
        }

        _cts.Dispose();
        _cts = null;
        _loopTask = null;
        _sdkReader.Disconnect();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var lastFlushUtc = DateTimeOffset.MinValue;
        var lastRaceFieldSyncUtc = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            var settings = _settings;

            if (!settings.TelemetryEnabled)
            {
                if (_sdkReader.IsConnected)
                {
                    _sdkReader.Disconnect();
                }

                PublishStatus(false, "Telemetrie-Erfassung deaktiviert.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            if (!_sdkReader.IsConnected)
            {
                if (!_sdkReader.TryConnect())
                {
                    PublishStatus(false, BuildWaitingStatus(settings));
                    await TryFlushOutboxAsync(settings, lastFlushUtc, ct).ConfigureAwait(false);
                    lastFlushUtc = DateTimeOffset.UtcNow;
                    await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                    continue;
                }
            }

            var frame = _sdkReader.TryReadNextFrame(out var metadataUpdated);
            if (metadataUpdated)
            {
                _tracker.UpdateMetadata(_sdkReader.CurrentMetadata);
            }

            if (frame is not null)
            {
                var batch = _tracker.ProcessFrame(frame);

                if (batch.LapsUpload is not null)
                {
                    _outbox.Enqueue("/api/v1/telemetry/laps", batch.LapsUpload, gzip: false);
                }

                if (batch.PitstopsUpload is not null)
                {
                    _outbox.Enqueue("/api/v1/telemetry/pitstops", batch.PitstopsUpload, gzip: false);
                }

                if (batch.TraceUpload is not null)
                {
                    _outbox.Enqueue("/api/v1/telemetry/trace", batch.TraceUpload, gzip: true);
                }

                if (batch.RaceFieldUpload is not null)
                {
                    _outbox.Enqueue("/api/v1/telemetry/race-field", batch.RaceFieldUpload, gzip: false);
                    lastRaceFieldSyncUtc = DateTimeOffset.UtcNow;
                }

                var meta = _sdkReader.CurrentMetadata;
                var sessionType = meta.ResolveSessionType(frame.SessionNum);
                PublishStatus(true, $"iRacing verbunden: {meta.TrackDisplayName} ({sessionType}) – Runde {frame.Lap}");

                // Im Rennen zusätzlich alle 60s den aktuellen Stint-Stand aller Fahrer synchronisieren
                if (sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow - lastRaceFieldSyncUtc > TimeSpan.FromSeconds(60))
                {
                    var snapshot = _tracker.BuildRaceFieldSnapshot();
                    if (snapshot is not null)
                    {
                        _outbox.Enqueue("/api/v1/telemetry/race-field", snapshot, gzip: false);
                        lastRaceFieldSyncUtc = DateTimeOffset.UtcNow;
                    }
                }
            }

            if (_outbox.PendingCount > 0 && DateTimeOffset.UtcNow - lastFlushUtc > TimeSpan.FromSeconds(5))
            {
                await TryFlushOutboxAsync(settings, lastFlushUtc, ct).ConfigureAwait(false);
                lastFlushUtc = DateTimeOffset.UtcNow;
            }

            // 60 Hz ≈ 16,6 ms
            await Task.Delay(15, ct).ConfigureAwait(false);
        }
    }

    private async Task TryFlushOutboxAsync(AppSettings settings, DateTimeOffset lastFlushUtc, CancellationToken ct)
    {
        if (!settings.IsPaired || string.IsNullOrWhiteSpace(settings.ServerBaseUrl) || _outbox.PendingCount == 0)
        {
            return;
        }

        if (DateTimeOffset.UtcNow - lastFlushUtc < TimeSpan.FromSeconds(4))
        {
            return;
        }

        await _outbox.FlushAsync(_apiClient, settings.ServerBaseUrl, settings.DeviceToken, ct).ConfigureAwait(false);
        PublishStatus(_lastConnected, _lastStatusText, force: true);
    }

    private static string BuildWaitingStatus(AppSettings settings)
    {
        if (!settings.IsPaired)
        {
            return "Warte auf Discord-Kopplung (/pair) und iRacing.";
        }

        return "Bereit – warte auf laufendes iRacing.";
    }

    private void PublishStatus(bool connected, string text, bool force = false)
    {
        if (!force && connected == _lastConnected && string.Equals(text, _lastStatusText, StringComparison.Ordinal))
        {
            return;
        }

        _lastConnected = connected;
        _lastStatusText = text;
        StatusChanged?.Invoke(new TelemetryRuntimeStatus(connected, text, _outbox.PendingCount));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _sdkReader.Dispose();
        _apiClient.Dispose();
    }
}
