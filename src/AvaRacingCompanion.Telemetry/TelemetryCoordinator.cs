using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Telemetry.Api;
using AvaRacingCompanion.Telemetry.Sdk;
using AvaRacingCompanion.Telemetry.Tracking;

namespace AvaRacingCompanion.Telemetry;

public sealed record TelemetryRuntimeStatus(
    bool IsConnectedToIRacing,
    bool IsDriverOnTrack,
    string StatusText,
    int PendingUploads);

/// <summary>
/// Hintergrund-Koordinator:
/// - Liest 60-Hz-Live-Telemetrie aus iRacing und puffert lokal in der Outbox.
/// - Account- & Spectator-/Replay-Schutz: Pausiert persönliche Uploads (laps, pitstops, trace),
///   sobald DriverUserID != paired_cust_id oder Spectator-/Replay-Modus aktiv ist.
/// - Lädt NIEMALS Daten hoch und scannt KEINE .ibt-Dateien, solange der Fahrer aktiv im Auto sitzt (IsOnTrack == true).
/// - Meldet 401-Token-Widerruf (/unpair) über <see cref="AuthRevoked"/> an die UI.
/// </summary>
public sealed class TelemetryCoordinator : IDisposable
{
    private readonly IRacingMemoryMappedReader _sdkReader = new();
    private readonly TelemetrySessionTracker _tracker = new();
    private readonly OfflineUploadOutbox _outbox;
    private readonly TelemetryApiClient _apiClient;
    private readonly IbtSyncService _ibtSyncService;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private AppSettings _settings = new();
    private string? _deviceToken;
    private string _lastStatusText = "Warte auf iRacing...";
    private bool _lastConnected;
    private volatile bool _isDriverOnTrack;
    private volatile bool _manualIbtScanRequested;

    public event Action<TelemetryRuntimeStatus>? StatusChanged;
    public event Action? AuthRevoked;

    public TelemetryCoordinator(
        OfflineUploadOutbox? outbox = null,
        TelemetryApiClient? apiClient = null,
        IbtSyncService? ibtSyncService = null)
    {
        _outbox = outbox ?? new OfflineUploadOutbox();
        _apiClient = apiClient ?? new TelemetryApiClient();
        _ibtSyncService = ibtSyncService ?? new IbtSyncService();
    }

    public int PendingUploads => _outbox.PendingCount;
    public bool IsDriverOnTrack => _isDriverOnTrack;

    public void UpdateSettings(AppSettings settings, string? deviceToken)
    {
        _settings = settings.Normalized();
        _deviceToken = deviceToken;
        _tracker.UploadTracesEnabled = _settings.UploadTracesEnabled;
    }

    public bool RequestManualIbtScan()
    {
        if (_isDriverOnTrack)
        {
            return false;
        }

        _manualIbtScanRequested = true;
        return true;
    }

    public void Start(AppSettings initialSettings, string? deviceToken)
    {
        UpdateSettings(initialSettings, deviceToken);
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
        var lastIbtScanUtc = DateTimeOffset.MinValue;
        var lastRaceFieldSyncUtc = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            var settings = _settings;
            var token = _deviceToken;

            if (!settings.TelemetryEnabled)
            {
                if (_sdkReader.IsConnected)
                {
                    _sdkReader.Disconnect();
                }

                _isDriverOnTrack = false;
                PublishStatus(false, false, "Telemetrie-Erfassung deaktiviert.");
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                continue;
            }

            if (!_sdkReader.IsConnected)
            {
                if (!_sdkReader.TryConnect())
                {
                    _isDriverOnTrack = false;

                    if (ShouldRunIbtScan(settings, lastIbtScanUtc))
                    {
                        RunIbtScan(settings, ct);
                        lastIbtScanUtc = DateTimeOffset.UtcNow;
                    }

                    if (await TryFlushOutboxAsync(settings, token, lastFlushUtc, ct).ConfigureAwait(false))
                    {
                        lastFlushUtc = DateTimeOffset.UtcNow;
                    }

                    PublishStatus(false, false, BuildWaitingStatus(token));
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
                var wasOnTrack = _isDriverOnTrack;
                _isDriverOnTrack = frame.IsOnTrack;

                var meta = _sdkReader.CurrentMetadata;
                var sessionType = meta.ResolveSessionType(frame.SessionNum);

                var isSpectatorOrReplay = frame.IsReplayPlaying || meta.IsPlayerSpectator;
                var isAccountMismatch = settings.PairedCustId.HasValue
                    && meta.PlayerCustId > 0
                    && meta.PlayerCustId != settings.PairedCustId.Value;
                var allowPersonalUploads = !isSpectatorOrReplay && !isAccountMismatch;

                var batch = _tracker.ProcessFrame(frame);

                if (allowPersonalUploads)
                {
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
                }

                if (!frame.IsReplayPlaying && batch.RaceFieldUpload is not null)
                {
                    _outbox.Enqueue("/api/v1/telemetry/race-field", batch.RaceFieldUpload, gzip: false);
                    lastRaceFieldSyncUtc = DateTimeOffset.UtcNow;
                }

                if (isAccountMismatch)
                {
                    PublishStatus(
                        true,
                        _isDriverOnTrack,
                        $"Account-Abweichung: iRacing CustID {meta.PlayerCustId} ≠ gekoppelt ({settings.PairedCustId}) – persönliche Uploads pausiert.");
                }
                else if (isSpectatorOrReplay)
                {
                    PublishStatus(
                        true,
                        false,
                        $"Spectator-/Replay-Modus ({meta.TrackDisplayName}) – persönliche Uploads pausiert.");
                }
                else if (_isDriverOnTrack)
                {
                    PublishStatus(
                        true,
                        true,
                        $"Im Auto (Uploads pausiert): {meta.TrackDisplayName} ({sessionType}) – Runde {frame.Lap}");
                }
                else
                {
                    if (wasOnTrack && sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase))
                    {
                        var snapshot = _tracker.BuildRaceFieldSnapshot();
                        if (snapshot is not null)
                        {
                            _outbox.Enqueue("/api/v1/telemetry/race-field", snapshot, gzip: false);
                            lastRaceFieldSyncUtc = DateTimeOffset.UtcNow;
                        }
                    }

                    PublishStatus(
                        true,
                        false,
                        $"iRacing im Menü (Uploads aktiv): {meta.TrackDisplayName} ({sessionType})");
                }

                if (!frame.IsReplayPlaying &&
                    sessionType.Contains("Race", StringComparison.OrdinalIgnoreCase) &&
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

            // Uploads & .ibt-Import NUR solange der Fahrer NICHT aktiv im Auto sitzt
            if (!_isDriverOnTrack)
            {
                if (ShouldRunIbtScan(settings, lastIbtScanUtc))
                {
                    RunIbtScan(settings, ct);
                    lastIbtScanUtc = DateTimeOffset.UtcNow;
                }

                if (_outbox.PendingCount > 0 && DateTimeOffset.UtcNow - lastFlushUtc > TimeSpan.FromSeconds(5))
                {
                    if (await TryFlushOutboxAsync(settings, token, lastFlushUtc, ct).ConfigureAwait(false))
                    {
                        lastFlushUtc = DateTimeOffset.UtcNow;
                    }
                }
            }

            await Task.Delay(15, ct).ConfigureAwait(false);
        }
    }

    private bool ShouldRunIbtScan(AppSettings settings, DateTimeOffset lastIbtScanUtc)
    {
        if (_manualIbtScanRequested)
        {
            _manualIbtScanRequested = false;
            return true;
        }

        if (!settings.ImportIbtFilesEnabled)
        {
            return false;
        }

        return DateTimeOffset.UtcNow - lastIbtScanUtc > TimeSpan.FromSeconds(30);
    }

    private void RunIbtScan(AppSettings settings, CancellationToken ct)
    {
        var result = _ibtSyncService.ScanAndEnqueueNewFiles(
            settings.IbtFolderPath,
            _outbox,
            settings.UploadTracesEnabled,
            pairedCustId: settings.PairedCustId,
            onProgress: msg => PublishStatus(_lastConnected, false, msg, force: true),
            cancellationToken: ct);

        if (result.FilesProcessed > 0)
        {
            PublishStatus(
                _lastConnected,
                false,
                $"{result.FilesProcessed} neue .ibt-Datei(en) verarbeitet ({result.PayloadsEnqueued} Pakete bereit zum Upload).",
                force: true);
        }
    }

    private async Task<bool> TryFlushOutboxAsync(
        AppSettings settings,
        string? deviceToken,
        DateTimeOffset lastFlushUtc,
        CancellationToken ct)
    {
        if (_isDriverOnTrack || string.IsNullOrWhiteSpace(deviceToken) || string.IsNullOrWhiteSpace(settings.ServerBaseUrl) || _outbox.PendingCount == 0)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - lastFlushUtc < TimeSpan.FromSeconds(4))
        {
            return false;
        }

        PublishStatus(_lastConnected, false, $"Lade {_outbox.PendingCount} Telemetrie-Paket(e) zum Bot-Server hoch ...", force: true);
        var flushResult = await _outbox.FlushAsync(_apiClient, settings.ServerBaseUrl, deviceToken, ct).ConfigureAwait(false);

        if (flushResult.AuthRevoked)
        {
            _deviceToken = null;
            PublishStatus(_lastConnected, false, "Geräte-Token wurde widerrufen (401). Bitte mit /pair neu koppeln.", force: true);
            AuthRevoked?.Invoke();
            return true;
        }

        PublishStatus(_lastConnected, false, _lastStatusText, force: true);
        return true;
    }

    private static string BuildWaitingStatus(string? deviceToken)
    {
        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            return "Warte auf Discord-Kopplung (/pair) und iRacing.";
        }

        return "Bereit – warte auf iRacing (Uploads & .ibt-Imports laufen nur außerhalb des Autos).";
    }

    private void PublishStatus(bool connected, bool onTrack, string text, bool force = false)
    {
        if (!force && connected == _lastConnected && string.Equals(text, _lastStatusText, StringComparison.Ordinal))
        {
            return;
        }

        _lastConnected = connected;
        _lastStatusText = text;
        StatusChanged?.Invoke(new TelemetryRuntimeStatus(connected, onTrack, text, _outbox.PendingCount));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _sdkReader.Dispose();
        _apiClient.Dispose();
    }
}
