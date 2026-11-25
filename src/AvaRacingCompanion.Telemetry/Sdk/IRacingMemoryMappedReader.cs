using System.IO.MemoryMappedFiles;
using System.Text;

namespace AvaRacingCompanion.Telemetry.Sdk;

/// <summary>
/// Rein lesender Zugriff auf das iRacing Shared-Memory SDK (Local\IRSDKMemMapFileName).
/// Keine Fremd-DLLs, keine Schreibzugriffe, im Leerlauf ohne laufendes iRacing praktisch 0 % CPU.
/// </summary>
public sealed class IRacingMemoryMappedReader : IDisposable
{
    private const string MemMapFileName = "Local\\IRSDKMemMapFileName";
    private const int HeaderSize = 112;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte[] _dataBuffer = Array.Empty<byte>();
    private readonly IRacingVarTable _varTable = new();

    private int _lastSessionInfoUpdate = -1;
    private int _lastTickCount = -1;
    private SessionMetadata _currentMetadata = SessionMetadata.Empty();

    public bool IsConnected => _accessor is not null;
    public SessionMetadata CurrentMetadata => _currentMetadata;

    public bool TryConnect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_accessor is not null)
        {
            return true;
        }

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MemMapFileName, MemoryMappedFileRights.Read);
            _accessor = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _lastSessionInfoUpdate = -1;
            _lastTickCount = -1;
            ReadVariableHeaders();
            return true;
        }
        catch
        {
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _lastSessionInfoUpdate = -1;
        _lastTickCount = -1;
        _currentMetadata = SessionMetadata.Empty();
    }

    /// <summary>
    /// Liest den neuesten 60-Hz-Frame, falls ein neuer Tick vorliegt.
    /// Gibt null zurück, wenn kein neuer Tick anliegt oder iRacing nicht verbunden ist.
    /// </summary>
    public TelemetryFrame? TryReadNextFrame(out bool metadataUpdated)
    {
        metadataUpdated = false;
        if (_accessor is null)
        {
            return null;
        }

        try
        {
            var status = _accessor.ReadInt32(4);
            if ((status & 1) == 0)
            {
                Disconnect();
                return null;
            }

            var sessionInfoUpdate = _accessor.ReadInt32(12);
            if (sessionInfoUpdate != _lastSessionInfoUpdate)
            {
                var infoLen = _accessor.ReadInt32(16);
                var infoOffset = _accessor.ReadInt32(20);
                if (infoLen > 0 && infoOffset > 0)
                {
                    var yamlBytes = new byte[infoLen];
                    _accessor.ReadArray(infoOffset, yamlBytes, 0, infoLen);
                    var nullIdx = Array.IndexOf(yamlBytes, (byte)0);
                    var yaml = Encoding.Latin1.GetString(yamlBytes, 0, nullIdx >= 0 ? nullIdx : yamlBytes.Length);
                    _currentMetadata = SessionInfoYamlParser.Parse(yaml);
                    _lastSessionInfoUpdate = sessionInfoUpdate;
                    metadataUpdated = true;
                    ReadVariableHeaders();
                }
            }

            var numBuf = Math.Clamp(_accessor.ReadInt32(32), 0, 4);
            var bufLen = _accessor.ReadInt32(36);
            if (numBuf == 0 || bufLen <= 0)
            {
                return null;
            }

            var latestTick = -1;
            var latestBufIndex = -1;
            var latestBufOffset = 0;

            for (var i = 0; i < numBuf; i++)
            {
                var baseOff = 48 + (i * 16);
                var tick = _accessor.ReadInt32(baseOff);
                var offset = _accessor.ReadInt32(baseOff + 4);
                if (tick > latestTick)
                {
                    latestTick = tick;
                    latestBufIndex = i;
                    latestBufOffset = offset;
                }
            }

            if (latestBufIndex < 0 || latestTick <= _lastTickCount)
            {
                return null;
            }

            if (_dataBuffer.Length < bufLen)
            {
                _dataBuffer = new byte[bufLen];
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                _accessor.ReadArray(latestBufOffset, _dataBuffer, 0, bufLen);
                var tickAfter = _accessor.ReadInt32(48 + (latestBufIndex * 16));
                if (tickAfter == latestTick)
                {
                    break;
                }

                latestTick = tickAfter;
            }

            _lastTickCount = latestTick;
            return _varTable.BuildFrame(_dataBuffer.AsSpan(0, bufLen), latestTick);
        }
        catch
        {
            Disconnect();
            return null;
        }
    }

    private void ReadVariableHeaders()
    {
        if (_accessor is null)
        {
            return;
        }

        var numVars = _accessor.ReadInt32(24);
        var varHeaderOffset = _accessor.ReadInt32(28);
        if (numVars <= 0 || varHeaderOffset < HeaderSize)
        {
            return;
        }

        var totalBytes = numVars * IRacingVarTable.VarHeaderSize;
        var rawHeaders = new byte[totalBytes];
        _accessor.ReadArray(varHeaderOffset, rawHeaders, 0, totalBytes);
        _varTable.LoadFromHeaderBytes(rawHeaders, numVars);
    }

    public void Dispose()
    {
        Disconnect();
    }
}
