using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AvaRacingCompanion.Core.Telemetry;
using AvaRacingCompanion.Core.Versioning;

namespace AvaRacingCompanion.Telemetry.Api;

public sealed record ApiCallResult(
    bool Success,
    int StatusCode,
    string? ErrorMessage = null)
{
    public bool IsAuthError => StatusCode is (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden;
}

public sealed record PairResult(
    bool Success,
    PairExchangeResponse? Response = null,
    string? ErrorMessage = null);

/// <summary>
/// HTTP-Client für die Bot-Ingest-API (v1) inkl. Gzip-Kompression für 60-Hz-Traces.
/// </summary>
public sealed class TelemetryApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public TelemetryApiClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"{AppInfo.AppId}/{AppInfo.GetCurrentVersion()}");
        }
    }

    public async Task<ApiCallResult> CheckHealthAsync(
        string serverBaseUrl,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUri(serverBaseUrl, "/api/v1/health", out var uri))
        {
            return new ApiCallResult(false, 0, "Ungültige Server-URL (muss mit https:// oder http:// beginnen).");
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ApiCallResult(false, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var health = JsonSerializer.Deserialize<HealthResponse>(json, JsonOptions);
            if (health is null || !string.Equals(health.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return new ApiCallResult(false, (int)response.StatusCode, "Unerwartete Health-Antwort.");
            }

            return new ApiCallResult(true, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new ApiCallResult(false, 0, ex.Message);
        }
    }

    public async Task<PairResult> ExchangePairingCodeAsync(
        string serverBaseUrl,
        string code,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUri(serverBaseUrl, "/api/v1/pair/exchange", out var uri))
        {
            return new PairResult(false, ErrorMessage: "Ungültige Server-URL.");
        }

        var normalizedCode = code.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(normalizedCode))
        {
            return new PairResult(false, ErrorMessage: "Bitte den 6-stelligen /pair-Code eingeben.");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new PairExchangeRequest(normalizedCode, deviceName), JsonOptions);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(uri, content, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new PairResult(
                    false,
                    ErrorMessage: $"Kopplung fehlgeschlagen (HTTP {(int)response.StatusCode}): {Truncate(body, 160)}");
            }

            var parsed = JsonSerializer.Deserialize<PairExchangeResponse>(body, JsonOptions);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Token))
            {
                return new PairResult(false, ErrorMessage: "Server hat kein gültiges Token zurückgegeben.");
            }

            return new PairResult(true, parsed);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new PairResult(false, ErrorMessage: ex.Message);
        }
    }

    public async Task<ApiCallResult> PostRawJsonAsync(
        string serverBaseUrl,
        string deviceToken,
        string relativePath,
        string jsonPayload,
        bool gzip,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildUri(serverBaseUrl, relativePath, out var uri))
        {
            return new ApiCallResult(false, 0, "Ungültige Server-URL.");
        }

        if (string.IsNullOrWhiteSpace(deviceToken))
        {
            return new ApiCallResult(false, (int)HttpStatusCode.Unauthorized, "Kein Device-Token hinterlegt.");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            if (gzip)
            {
                var compressed = CompressGzip(Encoding.UTF8.GetBytes(jsonPayload));
                var byteContent = new ByteArrayContent(compressed);
                byteContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                byteContent.Headers.ContentEncoding.Add("gzip");
                request.Content = byteContent;
            }
            else
            {
                request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new ApiCallResult(true, (int)response.StatusCode);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new ApiCallResult(false, (int)response.StatusCode, Truncate(body, 200));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ApiCallResult(false, 0, ex.Message);
        }
    }

    public static byte[] CompressGzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzipStream = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzipStream.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    private static bool TryBuildUri(string baseUrl, string relativePath, out Uri uri)
    {
        uri = default!;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return false;
        }

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        if (baseUri.Scheme is not ("https" or "http"))
        {
            return false;
        }

        return Uri.TryCreate($"{trimmed}/{relativePath.TrimStart('/')}", UriKind.Absolute, out uri!);
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length <= maxLength ? clean : clean[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
