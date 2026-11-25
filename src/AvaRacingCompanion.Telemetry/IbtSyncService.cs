using System.Text.Json;
using AvaRacingCompanion.Core.Versioning;
using AvaRacingCompanion.Telemetry.Api;
using AvaRacingCompanion.Telemetry.Sdk;
using AvaRacingCompanion.Telemetry.Tracking;

namespace AvaRacingCompanion.Telemetry;

public sealed record IbtScanResult(
    int FilesProcessed,
    int PayloadsEnqueued,
    int FilesSkippedLockedOrRecent);

/// <summary>
/// Scannt den lokalen iRacing-Telemetrie-Ordner (%USERPROFILE%\Documents\iRacing\telemetry)
/// nach neuen .ibt-Dateien, extrahiert Runden, Pitstops, 60-Hz-Traces und Race-Field-Daten
/// und stellt sie in die Offline-Outbox. Merkt sich bereits verarbeitete Dateien persistent.
/// </summary>
public sealed class IbtSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;
    private readonly TimeSpan _minFileAge;
    private HashSet<string> _processedKeys;

    public IbtSyncService(string? customStateFilePath = null, TimeSpan? minFileAge = null)
    {
        _minFileAge = minFileAge ?? TimeSpan.FromSeconds(5);

        if (!string.IsNullOrWhiteSpace(customStateFilePath))
        {
            _stateFilePath = customStateFilePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _stateFilePath = Path.Combine(appData, AppInfo.AppId, "processed_ibt.json");
        }

        _processedKeys = LoadProcessedKeys();
    }

    public static string GetDefaultTelemetryFolder()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "iRacing", "telemetry");
    }

    public IbtScanResult ScanAndEnqueueNewFiles(
        string? customFolderPath,
        OfflineUploadOutbox outbox,
        bool uploadTracesEnabled,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var folder = string.IsNullOrWhiteSpace(customFolderPath)
            ? GetDefaultTelemetryFolder()
            : customFolderPath.Trim();

        if (!Directory.Exists(folder))
        {
            return new IbtScanResult(0, 0, 0);
        }

        var files = Directory.GetFiles(folder, "*.ibt", SearchOption.TopDirectoryOnly)
            .OrderBy(f => File.GetLastWriteTimeUtc(f))
            .ToArray();

        var processedCount = 0;
        var enqueuedCount = 0;
        var skippedCount = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FileInfo info;
            try
            {
                info = new FileInfo(file);
            }
            catch
            {
                continue;
            }

            if (info.Length < 144)
            {
                continue;
            }

            var key = BuildFileKey(info);
            if (_processedKeys.Contains(key))
            {
                continue;
            }

            // Datei könnte gerade noch von iRacing geschrieben werden
            if (DateTime.UtcNow - info.LastWriteTimeUtc < _minFileAge || !CanOpenExclusively(file))
            {
                skippedCount++;
                continue;
            }

            try
            {
                onProgress?.Invoke($"Verarbeite .ibt-Datei: {info.Name} ...");
                var enqueuedForFile = ProcessSingleIbtFile(file, outbox, uploadTracesEnabled, cancellationToken);
                enqueuedCount += enqueuedForFile;
                processedCount++;

                _processedKeys.Add(key);
                SaveProcessedKeys();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Beschädigte/unvollständige Datei überspringen, ohne den restlichen Scan abzubrechen
                skippedCount++;
            }
        }

        return new IbtScanResult(processedCount, enqueuedCount, skippedCount);
    }

    private static int ProcessSingleIbtFile(
        string filePath,
        OfflineUploadOutbox outbox,
        bool uploadTracesEnabled,
        CancellationToken cancellationToken)
    {
        var tracker = new TelemetrySessionTracker
        {
            UploadTracesEnabled = uploadTracesEnabled
        };

        var enqueued = 0;
        var metadataApplied = false;

        // Erst Metadaten lesen und direkt alle Frames streamen
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Header + Metadata + Frames über ReadStreaming verarbeiten
        // Hinweis: ReadStreaming parst Metadata vor dem ersten onFrame-Aufruf;
        // wir nutzen einen kleinen Wrapper, um Metadata einmalig am Tracker zu setzen.
        var headerBuf = new byte[144];
        if (stream.Read(headerBuf, 0, headerBuf.Length) < 144)
        {
            return 0;
        }

        var header = IbtFileReader.ParseHeader(headerBuf);
        if (header.SessionInfoLen > 0 && header.SessionInfoOffset >= 144)
        {
            stream.Seek(header.SessionInfoOffset, SeekOrigin.Begin);
            var yamlBytes = new byte[header.SessionInfoLen];
            var read = stream.Read(yamlBytes, 0, yamlBytes.Length);
            var zeroIdx = Array.IndexOf(yamlBytes, (byte)0, 0, read);
            var yaml = System.Text.Encoding.Latin1.GetString(yamlBytes, 0, zeroIdx >= 0 ? zeroIdx : read);
            tracker.UpdateMetadata(SessionInfoYamlParser.Parse(yaml));
            metadataApplied = true;
        }

        stream.Seek(0, SeekOrigin.Begin);

        IbtFileReader.ReadStreaming(
            stream,
            frame =>
            {
                if (!metadataApplied)
                {
                    return;
                }

                var batch = tracker.ProcessFrame(frame);
                if (batch.LapsUpload is not null)
                {
                    outbox.Enqueue("/api/v1/telemetry/laps", batch.LapsUpload, gzip: false);
                    enqueued++;
                }

                if (batch.PitstopsUpload is not null)
                {
                    outbox.Enqueue("/api/v1/telemetry/pitstops", batch.PitstopsUpload, gzip: false);
                    enqueued++;
                }

                if (batch.TraceUpload is not null)
                {
                    outbox.Enqueue("/api/v1/telemetry/trace", batch.TraceUpload, gzip: true);
                    enqueued++;
                }

                if (batch.RaceFieldUpload is not null)
                {
                    outbox.Enqueue("/api/v1/telemetry/race-field", batch.RaceFieldUpload, gzip: false);
                    enqueued++;
                }
            },
            cancellationToken);

        // Am Ende der Race-.ibt einmal den finalen Stint-Stand aller Fahrer mitsenden
        var finalField = tracker.BuildRaceFieldSnapshot();
        if (finalField is not null)
        {
            outbox.Enqueue("/api/v1/telemetry/race-field", finalField, gzip: false);
            enqueued++;
        }

        return enqueued;
    }

    private static bool CanOpenExclusively(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFileKey(FileInfo info) =>
        $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";

    private HashSet<string> LoadProcessedKeys()
    {
        if (!File.Exists(_stateFilePath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_stateFilePath));
            return list is not null
                ? new HashSet<string>(list, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void SaveProcessedKeys()
    {
        var dir = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = $"{_stateFilePath}.tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_processedKeys.OrderBy(k => k), JsonOptions));
        File.Move(tmp, _stateFilePath, overwrite: true);
    }
}
