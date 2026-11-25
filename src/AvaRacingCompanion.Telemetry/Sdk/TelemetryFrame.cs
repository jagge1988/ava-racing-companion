namespace AvaRacingCompanion.Telemetry.Sdk;

public enum TrackSurface
{
    NotInWorld = -1,
    OffTrack = 0,
    InPitStall = 1,
    ApproachingPits = 2,
    OnTrack = 3
}

/// <summary>
/// Ein einzelner 60-Hz-Snapshot der relevanten iRacing-Live-Telemetrie.
/// Plattformunabhängig, damit die gesamte Auswertelogik auf macOS & Windows testbar bleibt.
/// </summary>
public sealed record TelemetryFrame
{
    public int TickCount { get; init; }
    public double SessionTimeSeconds { get; init; }
    public int SessionNum { get; init; }
    public int Lap { get; init; }
    public float LapLastLapTimeSeconds { get; init; }
    public float LapDistMeters { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public float SpeedMetersPerSec { get; init; }
    public float Throttle { get; init; }
    public float Brake { get; init; }
    public float SteeringWheelAngleRad { get; init; }
    public float VelocityX { get; init; }
    public float VelocityY { get; init; }
    public int Gear { get; init; }
    public float Rpm { get; init; }
    public float FuelLevelLiters { get; init; }
    public float TrackTempCelsius { get; init; }
    public int PlayerIncidents { get; init; }
    public int PlayerTireCompound { get; init; }
    public float LfWearL { get; init; } = 1f;
    public float LfWearM { get; init; } = 1f;
    public float LfWearR { get; init; } = 1f;
    public float RfWearL { get; init; } = 1f;
    public float RfWearM { get; init; } = 1f;
    public float RfWearR { get; init; } = 1f;
    public float LrWearL { get; init; } = 1f;
    public float LrWearM { get; init; } = 1f;
    public float LrWearR { get; init; } = 1f;
    public float RrWearL { get; init; } = 1f;
    public float RrWearM { get; init; } = 1f;
    public float RrWearR { get; init; } = 1f;
    public bool OnPitRoad { get; init; }
    public TrackSurface PlayerTrackSurface { get; init; } = TrackSurface.OnTrack;
    public int PitSvFlags { get; init; }
    public bool BrakeAbsActive { get; init; }
    public bool TractionControlActive { get; init; }
    public int DrsStatus { get; init; }
    public float ErsBatteryPct { get; init; }

    // Feld-Arrays für alle 64 Slots (relevant in Race-Sessions für /pitstops)
    public int[] CarIdxLap { get; init; } = Array.Empty<int>();
    public int[] CarIdxTireCompound { get; init; } = Array.Empty<int>();
    public bool[] CarIdxOnPitRoad { get; init; } = Array.Empty<bool>();
    public int[] CarIdxTrackSurface { get; init; } = Array.Empty<int>();
}
