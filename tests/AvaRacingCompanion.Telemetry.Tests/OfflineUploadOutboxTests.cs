using System.Net;
using AvaRacingCompanion.Telemetry.Api;
using Xunit;

namespace AvaRacingCompanion.Telemetry.Tests;

public sealed class OfflineUploadOutboxTests : IDisposable
{
    private readonly string _tempDir;

    public OfflineUploadOutboxTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ava-outbox-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task FlushAsync_SendsGzipAndPlainItemsFifoAndDeletesOnSuccess()
    {
        var outbox = new OfflineUploadOutbox(_tempDir);
        outbox.Enqueue("/api/v1/telemetry/laps", new { subsession_id = 1 }, gzip: false);
        outbox.Enqueue("/api/v1/telemetry/trace", new { sample_rate_hz = 60 }, gzip: true);
        Assert.Equal(2, outbox.PendingCount);

        var received = new List<(string Path, bool IsGzip, string? Auth)>();
        using var handler = new StubHttpMessageHandler(req =>
        {
            var isGzip = req.Content?.Headers.ContentEncoding.Contains("gzip") == true;
            received.Add((req.RequestUri!.AbsolutePath, isGzip, req.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        using var apiClient = new TelemetryApiClient(httpClient);

        var flush = await outbox.FlushAsync(apiClient, "https://bot.example.ts.net", "secret-token");

        Assert.Equal(2, flush.SentCount);
        Assert.False(flush.AuthRevoked);
        Assert.Equal(0, outbox.PendingCount);
        Assert.Equal("/api/v1/telemetry/laps", received[0].Path);
        Assert.False(received[0].IsGzip);
        Assert.Equal("Bearer secret-token", received[0].Auth);
        Assert.Equal("/api/v1/telemetry/trace", received[1].Path);
        Assert.True(received[1].IsGzip);
    }

    [Fact]
    public async Task FlushAsync_KeepsItemsOnServerFailure()
    {
        var outbox = new OfflineUploadOutbox(_tempDir);
        outbox.Enqueue("/api/v1/telemetry/laps", new { subsession_id = 1 }, gzip: false);

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        using var apiClient = new TelemetryApiClient(httpClient);

        var flush = await outbox.FlushAsync(apiClient, "https://bot.example.ts.net", "secret-token");

        Assert.Equal(0, flush.SentCount);
        Assert.False(flush.AuthRevoked);
        Assert.Equal(1, outbox.PendingCount);
    }

    [Fact]
    public async Task FlushAsync_ReportsAuthRevokedOn401AndKeepsItems()
    {
        var outbox = new OfflineUploadOutbox(_tempDir);
        outbox.Enqueue("/api/v1/telemetry/laps", new { subsession_id = 1 }, gzip: false);

        using var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var httpClient = new HttpClient(handler);
        using var apiClient = new TelemetryApiClient(httpClient);

        var flush = await outbox.FlushAsync(apiClient, "https://bot.example.ts.net", "revoked-token");

        Assert.Equal(0, flush.SentCount);
        Assert.True(flush.AuthRevoked);
        Assert.Equal(1, outbox.PendingCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
