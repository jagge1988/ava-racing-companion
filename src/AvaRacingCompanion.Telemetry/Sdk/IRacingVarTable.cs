using System.Runtime.InteropServices;
using System.Text;

namespace AvaRacingCompanion.Telemetry.Sdk;

/// <summary>
/// Gemeinsamer Dekoder für iRacing-Variablen-Header und 60-Hz-Datenpuffer.
/// Wird sowohl vom Live-Memory-Mapped-Reader als auch vom .ibt-Dateileser verwendet.
/// </summary>
public sealed class IRacingVarTable
{
    public const int VarHeaderSize = 144;
    private const int MaxCarIdx = 64;

    private sealed record VarDef(int Type, int Offset, int Count);

    private readonly Dictionary<string, VarDef> _vars = new(StringComparer.Ordinal);

    public int Count => _vars.Count;

    public void LoadFromHeaderBytes(ReadOnlySpan<byte> varHeaderBytes, int numVars)
    {
        _vars.Clear();
        for (var i = 0; i < numVars; i++)
        {
            var entryOffset = i * VarHeaderSize;
            if (entryOffset + VarHeaderSize > varHeaderBytes.Length)
            {
                break;
            }

            var entry = varHeaderBytes.Slice(entryOffset, VarHeaderSize);
            var type = MemoryMarshal.Read<int>(entry[..4]);
            var offset = MemoryMarshal.Read<int>(entry.Slice(4, 4));
            var count = MemoryMarshal.Read<int>(entry.Slice(8, 4));

            var nameSpan = entry.Slice(16, 32);
            var zeroIdx = nameSpan.IndexOf((byte)0);
            var name = Encoding.ASCII.GetString(zeroIdx >= 0 ? nameSpan[..zeroIdx] : nameSpan);
            if (!string.IsNullOrEmpty(name))
            {
                _vars[name] = new VarDef(type, offset, count);
            }
        }
    }

    public TelemetryFrame BuildFrame(ReadOnlySpan<byte> buffer, int tickCount)
    {
        var ersFraction = ReadFloat(buffer, "EnergyERSBatteryPct", defaultValue: -1f);
        var ersPct = ersFraction >= 0f ? ersFraction * 100f : 0f;

        return new TelemetryFrame
        {
            TickCount = tickCount,
            IsOnTrack = ReadBool(buffer, "IsOnTrack"),
            IsReplayPlaying = ReadBool(buffer, "IsReplayPlaying"),
            SessionTimeSeconds = ReadDouble(buffer, "SessionTime"),
            SessionNum = ReadInt(buffer, "SessionNum"),
            Lap = ReadInt(buffer, "Lap"),
            LapLastLapTimeSeconds = ReadFloat(buffer, "LapLastLapTime"),
            LapDistMeters = ReadFloat(buffer, "LapDist"),
            Lat = ReadDouble(buffer, "Lat"),
            Lon = ReadDouble(buffer, "Lon"),
            SpeedMetersPerSec = ReadFloat(buffer, "Speed"),
            Throttle = ReadFloat(buffer, "Throttle"),
            Brake = ReadFloat(buffer, "Brake"),
            SteeringWheelAngleRad = ReadFloat(buffer, "SteeringWheelAngle"),
            VelocityX = ReadFloat(buffer, "VelocityX"),
            VelocityY = ReadFloat(buffer, "VelocityY"),
            Gear = ReadInt(buffer, "Gear"),
            Rpm = ReadFloat(buffer, "RPM"),
            FuelLevelLiters = ReadFloat(buffer, "FuelLevel"),
            TrackTempCelsius = ReadFloat(buffer, "TrackTempCrew", ReadFloat(buffer, "TrackTemp")),
            PlayerIncidents = ReadInt(buffer, "PlayerCarMyIncidentCount"),
            PlayerTireCompound = ReadInt(buffer, "PlayerTireCompound", -1),
            LfWearL = ReadFloat(buffer, "LFwearL", 1f),
            LfWearM = ReadFloat(buffer, "LFwearM", 1f),
            LfWearR = ReadFloat(buffer, "LFwearR", 1f),
            RfWearL = ReadFloat(buffer, "RFwearL", 1f),
            RfWearM = ReadFloat(buffer, "RFwearM", 1f),
            RfWearR = ReadFloat(buffer, "RFwearR", 1f),
            LrWearL = ReadFloat(buffer, "LRwearL", 1f),
            LrWearM = ReadFloat(buffer, "LRwearM", 1f),
            LrWearR = ReadFloat(buffer, "LRwearR", 1f),
            RrWearL = ReadFloat(buffer, "RRwearL", 1f),
            RrWearM = ReadFloat(buffer, "RRwearM", 1f),
            RrWearR = ReadFloat(buffer, "RRwearR", 1f),
            OnPitRoad = ReadBool(buffer, "OnPitRoad"),
            PlayerTrackSurface = (TrackSurface)ReadInt(buffer, "PlayerTrackSurface", (int)TrackSurface.OnTrack),
            PitSvFlags = ReadInt(buffer, "PitSvFlags"),
            BrakeAbsActive = ReadBool(buffer, "BrakeABSactive"),
            TractionControlActive = false,
            DrsStatus = ReadInt(buffer, "DRS_Status"),
            ErsBatteryPct = ersPct,
            CarIdxLap = ReadIntArray(buffer, "CarIdxLap", MaxCarIdx),
            CarIdxTireCompound = ReadIntArray(buffer, "CarIdxTireCompound", MaxCarIdx),
            CarIdxOnPitRoad = ReadBoolArray(buffer, "CarIdxOnPitRoad", MaxCarIdx),
            CarIdxTrackSurface = ReadIntArray(buffer, "CarIdxTrackSurface", MaxCarIdx)
        };
    }

    private bool ReadBool(ReadOnlySpan<byte> buffer, string name)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset >= buffer.Length)
        {
            return false;
        }

        return buffer[v.Offset] != 0;
    }

    private int ReadInt(ReadOnlySpan<byte> buffer, string name, int defaultValue = 0)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset + 4 > buffer.Length)
        {
            return defaultValue;
        }

        return MemoryMarshal.Read<int>(buffer.Slice(v.Offset, 4));
    }

    private float ReadFloat(ReadOnlySpan<byte> buffer, string name, float defaultValue = 0f)
    {
        if (!_vars.TryGetValue(name, out var v) || v.Offset + 4 > buffer.Length)
        {
            return defaultValue;
        }

        return MemoryMarshal.Read<float>(buffer.Slice(v.Offset, 4));
    }

    private double ReadDouble(ReadOnlySpan<byte> buffer, string name, double defaultValue = 0.0)
    {
        if (!_vars.TryGetValue(name, out var v))
        {
            return defaultValue;
        }

        if (v.Type == 5 && v.Offset + 8 <= buffer.Length)
        {
            return MemoryMarshal.Read<double>(buffer.Slice(v.Offset, 8));
        }

        if (v.Type == 4 && v.Offset + 4 <= buffer.Length)
        {
            return MemoryMarshal.Read<float>(buffer.Slice(v.Offset, 4));
        }

        return defaultValue;
    }

    private int[] ReadIntArray(ReadOnlySpan<byte> buffer, string name, int maxCount)
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
            if (off + 4 <= buffer.Length)
            {
                result[i] = MemoryMarshal.Read<int>(buffer.Slice(off, 4));
            }
        }

        return result;
    }

    private bool[] ReadBoolArray(ReadOnlySpan<byte> buffer, string name, int maxCount)
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
            if (off < buffer.Length)
            {
                result[i] = buffer[off] != 0;
            }
        }

        return result;
    }
}
