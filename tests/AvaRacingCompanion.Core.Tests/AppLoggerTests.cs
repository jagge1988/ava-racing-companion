using AvaRacingCompanion.Core.Logging;
using Xunit;

namespace AvaRacingCompanion.Core.Tests;

public sealed class AppLoggerTests : IDisposable
{
    private readonly string _tempDir;

    public AppLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ava-log-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Logger_WritesEntriesAndRotatesWhenSizeLimitExceeded()
    {
        var logger = new AppLogger(_tempDir, maxBytes: 1024);

        for (var i = 0; i < 40; i++)
        {
            logger.Info("Test", $"Log-Eintrag Nummer {i} mit etwas Beispieltext fuer Rotation.");
        }

        Assert.True(File.Exists(logger.LogFilePath));
        Assert.True(File.Exists(Path.Combine(_tempDir, "companion.prev.log")));

        var currentContent = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("[INFO ] [Test]", currentContent, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
