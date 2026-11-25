using System.Runtime.InteropServices;
using System.Text;

namespace AvaRacingCompanion.Telemetry.Sdk;

public sealed record IbtFileHeader(
    int Version,
    int TickRate,
    int SessionInfoLen,
    int SessionInfoOffset,
    int NumVars,
    int VarHeaderOffset,
    int BufLen,
    int DataStartOffset,
    int SessionLapCount,
    int SessionRecordCount);

/// <summary>
/// Speicherschonender, abhängigkeitsfreier Leser für iRacing .ibt-Telemetriedateien.
/// Streamt 60-Hz-Frames einzeln, sodass selbst 300-MB-.ibt-Dateien kaum RAM benötigen.
/// </summary>
public static class IbtFileReader
{
    private const int HeaderAndSubHeaderSize = 144; // 112 (irsdk_header) + 32 (irsdk_diskSubHeader)

    public static (SessionMetadata Metadata, IReadOnlyList<TelemetryFrame> Frames) ReadAll(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadAll(stream);
    }

    public static (SessionMetadata Metadata, IReadOnlyList<TelemetryFrame> Frames) ReadAll(Stream stream)
    {
        var frames = new List<TelemetryFrame>();
        var metadata = ReadStreaming(stream, frame => frames.Add(frame));
        return (metadata, frames);
    }

    /// <summary>
    /// Liest Metadaten und streamt jeden 60-Hz-Frame direkt an den Callback (minimaler RAM-Verbrauch).
    /// </summary>
    public static SessionMetadata ReadStreaming(
        string filePath,
        Action<TelemetryFrame> onFrame,
        CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadStreaming(stream, onFrame, cancellationToken);
    }

    public static SessionMetadata ReadStreaming(
        Stream stream,
        Action<TelemetryFrame> onFrame,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onFrame);

        var headerBuf = new byte[HeaderAndSubHeaderSize];
        ReadExact(stream, headerBuf);
        var header = ParseHeader(headerBuf);

        if (header.BufLen <= 0 || header.NumVars <= 0)
        {
            return SessionMetadata.Empty();
        }

        // 1. SessionInfo YAML lesen
        var metadata = SessionMetadata.Empty();
        if (header.SessionInfoLen > 0 && header.SessionInfoOffset >= HeaderAndSubHeaderSize)
        {
            stream.Seek(header.SessionInfoOffset, SeekOrigin.Begin);
            var yamlBytes = new byte[header.SessionInfoLen];
            ReadExact(stream, yamlBytes);
            var zeroIdx = Array.IndexOf(yamlBytes, (byte)0);
            var yaml = Encoding.Latin1.GetString(yamlBytes, 0, zeroIdx >= 0 ? zeroIdx : yamlBytes.Length);
            metadata = SessionInfoYamlParser.Parse(yaml);
        }

        // 2. Variablen-Header lesen
        stream.Seek(header.VarHeaderOffset, SeekOrigin.Begin);
        var varHeaderBytes = new byte[header.NumVars * IRacingVarTable.VarHeaderSize];
        ReadExact(stream, varHeaderBytes);

        var varTable = new IRacingVarTable();
        varTable.LoadFromHeaderBytes(varHeaderBytes, header.NumVars);

        // 3. 60-Hz-Frames sequenziell streamen
        stream.Seek(header.DataStartOffset, SeekOrigin.Begin);
        var sampleBuf = new byte[header.BufLen];
        var maxPossibleRecords = (int)((stream.Length - header.DataStartOffset) / header.BufLen);
        var recordCount = header.SessionRecordCount > 0
            ? Math.Min(header.SessionRecordCount, maxPossibleRecords)
            : maxPossibleRecords;

        for (var tick = 0; tick < recordCount; tick++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = ReadFully(stream, sampleBuf);
            if (read < header.BufLen)
            {
                break;
            }

            onFrame(varTable.BuildFrame(sampleBuf, tick));
        }

        return metadata;
    }

    public static IbtFileHeader ParseHeader(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < HeaderAndSubHeaderSize)
        {
            throw new InvalidDataException("Ungültige .ibt-Datei (Header zu kurz).");
        }

        return new IbtFileHeader(
            Version: MemoryMarshal.Read<int>(headerBytes.Slice(0, 4)),
            TickRate: MemoryMarshal.Read<int>(headerBytes.Slice(8, 4)),
            SessionInfoLen: MemoryMarshal.Read<int>(headerBytes.Slice(16, 4)),
            SessionInfoOffset: MemoryMarshal.Read<int>(headerBytes.Slice(20, 4)),
            NumVars: MemoryMarshal.Read<int>(headerBytes.Slice(24, 4)),
            VarHeaderOffset: MemoryMarshal.Read<int>(headerBytes.Slice(28, 4)),
            BufLen: MemoryMarshal.Read<int>(headerBytes.Slice(36, 4)),
            DataStartOffset: MemoryMarshal.Read<int>(headerBytes.Slice(52, 4)),
            SessionLapCount: MemoryMarshal.Read<int>(headerBytes.Slice(136, 4)),
            SessionRecordCount: MemoryMarshal.Read<int>(headerBytes.Slice(140, 4)));
    }

    private static void ReadExact(Stream stream, byte[] buffer)
    {
        var total = ReadFully(stream, buffer);
        if (total < buffer.Length)
        {
            throw new EndOfStreamException("Unerwartetes Dateiende beim Lesen der .ibt-Datei.");
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
