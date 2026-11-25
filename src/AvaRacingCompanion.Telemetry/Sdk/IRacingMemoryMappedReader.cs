using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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
    private const int VarHeaderSize = 144;
    private const int MaxCarIdx = 64;

    private sealed record VarDef(int Type, int Offset, int Count);

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte[] _dataBuffer = Array.Empty<byte>();
    private readonly Dictionary<string, VarDef> _vars = new(StringComparer.Ordinal);

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
        _vars.Clear();
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

            // Lock-freie Konsistenzprüfung: TickCount vor und nach dem Kopieren vergleichen
            var tickAfter = -1;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                _accessor.ReadArray(latestBufOffset, _dataBuffer, 0, bufLen);
                tickAfter = _accessor.ReadInt32(48 + (latestBufIndex * 16));
                if (tickAfter == latestTick)
                {
                    break;
                }

                latestTick = tickAfter;
            }

            _lastTickCount = latestTick;
            return BuildFrame(latestTick);
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

        _vars.Clear();
        var numVars = _accessor.ReadInt32(24);
        var varHeaderOffset = _accessor.ReadInt32(28);
        if (numVars <= 0 || varHeaderOffset < HeaderSize)
        {
            return;
        }

        var nameBuf = new byte[32];
        for (var i = 0; i < numVars; i++)
        {
            var entryOffset = varHeaderOffset + (i * VarHeaderSize);
            var type = _accessor.ReadInt32(entryOffset);
            var offset = _accessor.ReadInt32(entryOffset + 4);
            var count = _accessor.ReadInt32(entryOffset + 8);
            _accessor.ReadArray(entryOffset + 16, nameBuf, 0, 32);

            var zeroIdx = Array.IndexOf(nameBuf, (byte)0);
            var name = Encoding.ASCII.GetString(nameBuf, 0, zeroIdx >= 0 ? zeroIdx : nameBuf.Length);
            if (!string.IsNullOrEmpty(name))
            {
                _vars[name] = new VarDef(type, offset, count);
            }
        }
    }

    private TelemetryFrame BuildFrame(int tickCount)
    {
        var ersFraction = ReadFloat("EnergyERSBatteryPct", defaultValue: -1f);
        var ersPct = ersFraction >= 0f ? ersFraction * 100f : 0f;

        return new TelemetryFrame
        {
            TickCount = tickCount,
            SessionTimeSeconds = ReadDouble("SessionTime"),
            SessionNum = ReadInt("SessionNum"),
            Lap = ReadInt("Lap"),
            LapLastLapTimeSeconds = ReadFloat("LapLastLapTime"),
            LapDistMeters = ReadFloat("LapDist"),
            Lat = ReadDouble("Lat"),
            Lon = ReadDouble("Lon"),
            SpeedMetersPerSec = ReadFloat("Speed"),
            Throttle = ReadFloat("Throttle"),
            Brake = ReadFloat("Brake"),
            SteeringWheelAngleRad = ReadFloat("SteeringWheelAngle"),
            VelocityX = ReadFloat("VelocityX"),
            VelocityY = ReadFloat("VelocityY"),
            Gear = ReadInt("Gear"),
            Rpm = ReadFloat("RPM"),
            FuelLevelLiters = ReadFloat("FuelLevel"),
            TrackTempCelsius = ReadFloat("TrackTempCrew", ReadFloat("TrackTemp")),
            PlayerIncidents = ReadInt("PlayerCarMyIncidentCount"),
            PlayerTireCompound = ReadInt("PlayerTireCompound", -1),
            LfWearL = ReadFloat("LFwearL", 1f),
            LfWearM = ReadFloat("LFwearM", 1f),
            LfWearR = ReadFloat("LFwearR", 1f),
            RfWearL = ReadFloat("RFwearL", 1f),
            RfWearM = ReadFloat("RFwearM", 1f),
            RfWearR = ReadFloat("RFwearR", 1f),
            LrWearL = ReadFloat("LRwearL", 1f),
            LrWearM = ReadFloat("LRwearM", 1f),
            LrWearR = ReadFloat("LRwearR", 1f),
            RrWearL = ReadFloat("RRwearL", 1f),
            RrWearM = ReadFloat("RRwearM", 1f),
            RrWearR = ReadFloat("RRwearR", 1f),
            OnPitRoad = ReadBool("OnPitRoad"),
            PlayerTrackSurface = (TrackSurface)ReadInt("PlayerTrackSurface", (int)TrackSurface.OnTrack),
            PitSvFlags = ReadInt("PitSvFlags"),
            BrakeAbsActive = ReadBool("BrakeABSactive"),
            TractionControlActive = false,
            DrsStatus = ReadInt("DRS_Status"),
            ErsBatteryPct = ersPct,
            CarIdxLap = ReadIntArray("CarIdxLap", MaxCarIdx),
            CarIdxTireCompound = ReadIntArray("CarIdxTireCompound", MaxCarIdx),
            CarIdxOnPitRoad = ReadBoolArray("CarIdxOnPitRoad", MaxCarIdx),
            CarIdxTrackSurface = ReadIntArray("CarIdxTrackSurface", MaxCarIdx)
        };
    }

    private bool ReadBool(string name)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset >= _dataBuffer.Length)
        {
            return false;
        }

        return _dataBuffer[v.Offset] != 0;
    }

    private int ReadInt(string name, int defaultValue = 0)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset + 4 > _dataBuffer.Length)
        {
            return defaultValue;
        }

        return MemoryMarshal.Read<int>(_dataBuffer.AsSpan(v.Offset, 4));
    }

    private float ReadFloat(string name, float defaultValue = 0f)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset + 4 > _dataBuffer.Length)
        {
            return defaultValue;
        }

        return MemoryMarshal.Read<float>(_dataBuffer.AsSpan(v.Offset, 4));
    }

    private double ReadDouble(string name, double defaultValue = 0.0)
    {
        if (!_vars.TryGetValue(name, out var v))
        {
            return defaultValue;
        }

        if (v.Type == 5 && v.Offset + 8 <= _dataBuffer.Length)
        {
            return MemoryMarshal.Read<double>(_dataBuffer.AsSpan(v.Offset, 8));
        }

        if (v.Type == 4 && v.Offset + 4 <= _dataBuffer.Length)
        {
            return MemoryMarshal.Read<float>(_dataBuffer.AsSpan(v.Offset, 4));
        }

        return defaultValue;
    }

    private int[] ReadIntArray(string name, int maxCount)
    {
        if (!_vars.TryGetValue(name, out var v))
        {
            return Array.Empty<int>();
        }

        var count = Math.Min(v.Count, maxCount);
        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var off = v.Offset + (i * 4);
            if (off + 4 <= _dataBuffer.Length)
            {
                result[i] = MemoryMarshal.Read<int>(_dataBuffer.AsSpan(off, 4));
            }
        }

        return result;
    }

    private bool[] ReadBoolArray(string name, int maxCount)
    {
        if (!_vars.TryGetValue(name, out var v))
        {
            return Array.Empty<bool>();
        }

        var count = Math.Min(v.Count, maxCount);
        var result = new bool[count];
        for (var i = 0; i < count; i++)
        {
            var off = v.Offset + i;
            if (off < _dataBuffer.Length)
            {
                result[i] = _dataBuffer[off] != 0;
            }
        }

        return result;
    }

    public void Dispose()
    {
        Disconnect();
    }
}
