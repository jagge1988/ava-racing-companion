using System.Text;
using AvaRacingCompanion.Core.Versioning;

namespace AvaRacingCompanion.Core.Logging;

/// <summary>
/// Thread-sicherer, abhängigkeitsfreier Datei-Logger mit automatischer Rotation
/// unter %APPDATA%\AvaRacingCompanion\logs\companion.log.
/// Darf niemals Secrets oder Bearer-Tokens protokollieren.
/// </summary>
public sealed class AppLogger
{
    private const long DefaultMaxBytes = 2 * 1024 * 1024; // 2 MB

    private readonly object _sync = new();
    private readonly long _maxBytes;

    public string LogDirectory { get; }
    public string LogFilePath { get; }

    public static AppLogger Shared { get; } = new();

    public AppLogger(string? customDirectory = null, long maxBytes = DefaultMaxBytes)
    {
        _maxBytes = Math.Max(1024, maxBytes);

        if (!string.IsNullOrWhiteSpace(customDirectory))
        {
            LogDirectory = customDirectory;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            LogDirectory = Path.Combine(appData, AppInfo.AppId, "logs");
        }

        LogFilePath = Path.Combine(LogDirectory, "companion.log");
    }

    public void Info(string category, string message) => Write("INFO", category, message);

    public void Warn(string category, string message) => Write("WARN", category, message);

    public void Error(string category, string message, Exception? ex = null)
    {
        var full = ex is null
            ? message
            : $"{message} | {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", category, full);
    }

    private void Write(string level, string category, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] [{category}] {Sanitize(message)}{Environment.NewLine}";

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
            catch
            {
                // Logging darf die Anwendung niemals zum Absturz bringen
            }
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(LogFilePath))
        {
            return;
        }

        var info = new FileInfo(LogFilePath);
        if (info.Length < _maxBytes)
        {
            return;
        }

        var archivePath = Path.Combine(LogDirectory, "companion.prev.log");
        File.Move(LogFilePath, archivePath, overwrite: true);
    }

    private static string Sanitize(string input) =>
        input.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
