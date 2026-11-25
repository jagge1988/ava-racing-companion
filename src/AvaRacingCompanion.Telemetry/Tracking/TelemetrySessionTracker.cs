using AvaRacingCompanion.Core.Telemetry;
using AvaRacingCompanion.Telemetry.Sdk;

namespace AvaRacingCompanion.Telemetry.Tracking;

public sealed record TrackerBatchOutput(
    LapsUploadRequest? LapsUpload = null,
    PitstopsUploadRequest? PitstopsUpload = null,
    LapTraceUploadRequest? TraceUpload = null,
    RaceFieldUploadRequest? RaceFieldUpload = null);

/// <summary>
/// Zustandsautomat über 60-Hz-Frames:
/// - In Practice/Quali/Race: erfasst Runden, Sprit, Reifenverschleiß, Pitstops und volle 60-Hz-Traces des aktiven Fahrers.
/// - Zusätzlich in Race-Sessions: verfolgt Stints, Reifensätze und Boxenstopps aller Fahrer im Feld (CarIdx*).
/// </summary>
public sealed class TelemetrySessionTracker
{
    private const double StationarySpeedThresholdMs = 0.5;
    private const int MinTraceSamplesPerLap = 60;
    private const int MaxTraceSamplesPerLap = 60 * 600; // 10 Minuten Sicherheitslimit (z. B. Nordschleife)

    private sealed class LapTraceBuffer
    {
        public readonly List<int> TimeMs = new(8192);
        public readonly List<double> LapDistM = new(8192);
        public readonly List<double> Lat = new(8192);
        public readonly List<double> Lon = new(8192);
        public readonly List<double> SpeedKmh = new(8192);
        public readonly List<double> Throttle = new(8192);
        public readonly List<double> Brake = new(8192);
        public readonly List<double> SteeringRad = new(8192);
        public readonly List<double> SlipAngleDeg = new(8192);
        public readonly List<int> Gear = new(8192);
        public readonly List<int> Rpm = new(8192);
        public readonly List<double> ErsPct = new(8192);
        public readonly List<byte> Flags = new(8192);

        public void Clear()
        {
            TimeMs.Clear();
            LapDistM.Clear();
            Lat.Clear();
            Lon.Clear();
            SpeedKmh.Clear();
            Throttle.Clear();
            Brake.Clear();
            SteeringRad.Clear();
            SlipAngleDeg.Clear();
            Gear.Clear();
            Rpm.Clear();
            ErsPct.Clear();
            Flags.Clear();
        }
    }

    private sealed class FieldDriverState
    {
        public int CustId { get; init; }
        public string CarNumber { get; init; } = string.Empty;
        public bool OnPitRoad { get; set; }
        public double PitEntryTime { get; set; }
        public double StationaryTimeS { get; set; }
        public int PitStartLap { get; set; }
        public string CompoundBeforePit { get; set; } = "Unknown";

        public int CurrentStintNumber { get; set; }
        public int CurrentStintStartLap { get; set; }
        public int CurrentStintLastLap { get; set; }
        public string CurrentCompound { get; set; } = "Unknown";

        public List<RaceFieldStintDto> CompletedStints { get; } = new();
        public List<RaceFieldPitstopDto> Pitstops { get; } = new();
    }

    private SessionMetadata _metadata = SessionMetadata.Empty();
    private int _currentSessionNum = -1;
    private double _lastSessionTime;

    // Aktiver Fahrer – Rundenstatus
    private int _currentLap = -1;
    private double _lapStartSessionTime;
    private float _lapStartFuel;
    private int _lapStartIncidents;
    private bool _lapWasInlap;
    private bool _lapWasOutlap;
    private bool _lapHadOfftrack;
    private int _tireAgeLaps;
    private int _lastCompoundIndex = -1;
    private readonly LapTraceBuffer _trace = new();

    // Aktiver Fahrer – Pitstop-Status
    private bool _playerOnPitRoad;
    private double _playerPitEntryTime;
    private double _playerPitStationaryS;
    private float _playerFuelBeforePit;
    private double _playerWearLfBeforePit = 1.0;
    private double _playerWearRfBeforePit = 1.0;
    private double _playerWearLrBeforePit = 1.0;
    private double _playerWearRrBeforePit = 1.0;
    private int _playerPitStartLap;
    private int _playerLastPitSvFlags;

    // Feld-Tracking für Race-Sessions
    private readonly Dictionary<int, FieldDriverState> _fieldDrivers = new();

    public bool UploadTracesEnabled { get; set; } = true;

    public void UpdateMetadata(SessionMetadata metadata)
    {
        if (metadata.SubsessionId != _metadata.SubsessionId)
        {
            ResetSessionState();
        }

        _metadata = metadata;
    }

    public TrackerBatchOutput ProcessFrame(TelemetryFrame frame)
    {
        var dt = _lastSessionTime > 0 && frame.SessionTimeSeconds > _lastSessionTime
            ? Math.Min(frame.SessionTimeSeconds - _lastSessionTime, 0.5)
            : 0.0;
        _lastSessionTime = frame.SessionTimeSeconds;

        if (frame.SessionNum != _currentSessionNum)
        {
            ResetSessionState();
            _currentSessionNum = frame.SessionNum;
        }

        LapsUploadRequest? lapsUpload = null;
        PitstopsUploadRequest? pitstopsUpload = null;
        LapTraceUploadRequest? traceUpload = null;
        RaceFieldUploadRequest? raceFieldUpload = null;

        // 1. Aktiver Fahrer: Pitstop-Erkennung
        var pitstop = UpdatePlayerPitstop(frame, dt);
        if (pitstop is not null && _metadata.SubsessionId > 0)
        {
            pitstopsUpload = new PitstopsUploadRequest(_metadata.SubsessionId, new[] { pitstop });
        }

        // 2. Aktiver Fahrer: Rundenwechsel & 60-Hz-Trace
        if (frame.Lap > 0)
        {
            if (_currentLap < 0)
            {
                BeginLap(frame, isOutlap: frame.OnPitRoad || frame.PlayerTrackSurface == TrackSurface.InPitStall);
            }
            else if (frame.Lap > _currentLap)
            {
                var (completedLap, completedTrace) = CompleteLap(frame);
                if (completedLap is not null && _metadata.SubsessionId > 0)
                {
                    lapsUpload = new LapsUploadRequest(
                        SubsessionId: _metadata.SubsessionId,
                        SessionNum: frame.SessionNum,
                        SessionType: _metadata.ResolveSessionType(frame.SessionNum),
                        TrackId: _metadata.TrackId,
                        CarId: _metadata.PlayerCarId,
                        Laps: new[] { completedLap });
                }

                traceUpload = completedTrace;
                BeginLap(frame, isOutlap: frame.OnPitRoad);
            }

            RecordActiveLapSample(frame);
        }

        // 3. Race-Session: komplettes Fahrerfeld (CarIdx*) für /pitstops
        if (IsRaceSession(frame.SessionNum))
        {
            var fieldChanged = UpdateRaceField(frame, dt);
            if (fieldChanged && _metadata.SubsessionId > 0)
            {
                raceFieldUpload = BuildRaceFieldSnapshot();
            }
        }

        return new TrackerBatchOutput(lapsUpload, pitstopsUpload, traceUpload, raceFieldUpload);
    }

    public RaceFieldUploadRequest? BuildRaceFieldSnapshot()
    {
        if (_metadata.SubsessionId <= 0 || _fieldDrivers.Count == 0)
        {
            return null;
        }

        var drivers = new List<RaceFieldDriverDto>();
        foreach (var state in _fieldDrivers.Values)
        {
            var stints = new List<RaceFieldStintDto>(state.CompletedStints);
            if (state.CurrentStintNumber > 0 && state.CurrentStintLastLap >= state.CurrentStintStartLap)
            {
                stints.Add(new RaceFieldStintDto(
                    StintNumber: state.CurrentStintNumber,
                    StartLap: state.CurrentStintStartLap,
                    EndLap: state.CurrentStintLastLap,
                    LapsOnCompound: state.CurrentStintLastLap - state.CurrentStintStartLap + 1,
                    TireCompound: state.CurrentCompound));
            }

            if (stints.Count == 0 && state.Pitstops.Count == 0)
            {
                continue;
            }

            drivers.Add(new RaceFieldDriverDto(
                CustId: state.CustId,
                CarNumber: state.CarNumber,
                Stints: stints,
                Pitstops: state.Pitstops.ToArray()));
        }

        return drivers.Count > 0
            ? new RaceFieldUploadRequest(_metadata.SubsessionId, drivers)
            : null;
    }

    private void BeginLap(TelemetryFrame frame, bool isOutlap)
    {
        _currentLap = frame.Lap;
        _lapStartSessionTime = frame.SessionTimeSeconds;
        _lapStartFuel = frame.FuelLevelLiters;
        _lapStartIncidents = frame.PlayerIncidents;
        _lapWasInlap = false;
        _lapWasOutlap = isOutlap;
        _lapHadOfftrack = false;
        _trace.Clear();

        if (_lastCompoundIndex >= 0 && frame.PlayerTireCompound >= 0 && frame.PlayerTireCompound != _lastCompoundIndex)
        {
            _tireAgeLaps = 0;
        }

        if (frame.PlayerTireCompound >= 0)
        {
            _lastCompoundIndex = frame.PlayerTireCompound;
        }
    }

    private (LapTelemetryDto? Lap, LapTraceUploadRequest? Trace) CompleteLap(TelemetryFrame frame)
    {
        var lapNumber = _currentLap;
        var lapTimeMs = frame.LapLastLapTimeSeconds > 0.5f
            ? (int)Math.Round(frame.LapLastLapTimeSeconds * 1000.0)
            : (int)Math.Round(Math.Max(0.0, frame.SessionTimeSeconds - _lapStartSessionTime) * 1000.0);

        if (lapTimeMs <= 0)
        {
            return (null, null);
        }

        _tireAgeLaps++;

        var fuelUsed = Math.Max(0.0, _lapStartFuel - frame.FuelLevelLiters);
        var incidentsDelta = Math.Max(0, frame.PlayerIncidents - _lapStartIncidents);
        var isClean = !_lapWasInlap && !_lapWasOutlap && incidentsDelta == 0 && !_lapHadOfftrack;

        var lapDto = new LapTelemetryDto(
            LapNumber: lapNumber,
            LapTimeMs: lapTimeMs,
            FuelStartL: Math.Round(_lapStartFuel, 3),
            FuelEndL: Math.Round(frame.FuelLevelLiters, 3),
            FuelUsedL: Math.Round(fuelUsed, 3),
            TireCompound: _metadata.ResolveTireCompound(frame.PlayerTireCompound),
            TireAgeLaps: _tireAgeLaps,
            WearLf: TelemetryMath.AverageWear(frame.LfWearL, frame.LfWearM, frame.LfWearR),
            WearRf: TelemetryMath.AverageWear(frame.RfWearL, frame.RfWearM, frame.RfWearR),
            WearLr: TelemetryMath.AverageWear(frame.LrWearL, frame.LrWearM, frame.LrWearR),
            WearRr: TelemetryMath.AverageWear(frame.RrWearL, frame.RrWearM, frame.RrWearR),
            TrackTempC: Math.Round(frame.TrackTempCelsius, 1),
            IncidentsDelta: incidentsDelta,
            IsClean: isClean,
            IsInlap: _lapWasInlap,
            IsOutlap: _lapWasOutlap);

        LapTraceUploadRequest? traceDto = null;
        if (UploadTracesEnabled && _metadata.SubsessionId > 0 && _trace.TimeMs.Count >= MinTraceSamplesPerLap)
        {
            traceDto = new LapTraceUploadRequest(
                SampleRateHz: 60,
                SubsessionId: _metadata.SubsessionId,
                LapNumber: lapNumber,
                TrackLengthM: _metadata.TrackLengthMeters,
                TimeMs: _trace.TimeMs.ToArray(),
                LapDistM: _trace.LapDistM.ToArray(),
                Lat: _trace.Lat.ToArray(),
                Lon: _trace.Lon.ToArray(),
                SpeedKmh: _trace.SpeedKmh.ToArray(),
                Throttle: _trace.Throttle.ToArray(),
                Brake: _trace.Brake.ToArray(),
                SteeringRad: _trace.SteeringRad.ToArray(),
                SlipAngleDeg: _trace.SlipAngleDeg.ToArray(),
                Gear: _trace.Gear.ToArray(),
                Rpm: _trace.Rpm.ToArray(),
                ErsPct: _trace.ErsPct.ToArray(),
                Flags: _trace.Flags.ToArray());
        }

        return (lapDto, traceDto);
    }

    private void RecordActiveLapSample(TelemetryFrame frame)
    {
        if (frame.OnPitRoad)
        {
            _lapWasInlap = true;
        }

        if (frame.PlayerTrackSurface == TrackSurface.OffTrack)
        {
            _lapHadOfftrack = true;
        }

        if (!UploadTracesEnabled || _trace.TimeMs.Count >= MaxTraceSamplesPerLap)
        {
            return;
        }

        var flags = TelemetryFlags.None;
        if (frame.DrsStatus >= 2) flags |= TelemetryFlags.Drs;
        if (frame.BrakeAbsActive) flags |= TelemetryFlags.Abs;
        if (frame.TractionControlActive) flags |= TelemetryFlags.Tc;
        if (frame.PlayerTrackSurface == TrackSurface.OffTrack) flags |= TelemetryFlags.Offtrack;
        if (frame.OnPitRoad) flags |= TelemetryFlags.Pit;

        var relTimeMs = (int)Math.Round(Math.Max(0.0, frame.SessionTimeSeconds - _lapStartSessionTime) * 1000.0);

        _trace.TimeMs.Add(relTimeMs);
        _trace.LapDistM.Add(Math.Round(frame.LapDistMeters, 2));
        _trace.Lat.Add(Math.Round(frame.Lat, 7));
        _trace.Lon.Add(Math.Round(frame.Lon, 7));
        _trace.SpeedKmh.Add(Math.Round(frame.SpeedMetersPerSec * 3.6, 2));
        _trace.Throttle.Add(Math.Round(Math.Clamp(frame.Throttle, 0f, 1f) * 100.0, 1));
        _trace.Brake.Add(Math.Round(Math.Clamp(frame.Brake, 0f, 1f) * 100.0, 1));
        _trace.SteeringRad.Add(Math.Round(frame.SteeringWheelAngleRad, 4));
        _trace.SlipAngleDeg.Add(TelemetryMath.ComputeSlipAngleDeg(frame.VelocityX, frame.VelocityY));
        _trace.Gear.Add(frame.Gear);
        _trace.Rpm.Add((int)Math.Round(frame.Rpm));
        _trace.ErsPct.Add(Math.Round(Math.Clamp(frame.ErsBatteryPct, 0f, 100f), 1));
        _trace.Flags.Add((byte)flags);
    }

    private PitstopTelemetryDto? UpdatePlayerPitstop(TelemetryFrame frame, double dt)
    {
        if (!_playerOnPitRoad && frame.OnPitRoad)
        {
            _playerOnPitRoad = true;
            _playerPitEntryTime = frame.SessionTimeSeconds;
            _playerPitStationaryS = 0.0;
            _playerFuelBeforePit = frame.FuelLevelLiters;
            _playerWearLfBeforePit = TelemetryMath.AverageWear(frame.LfWearL, frame.LfWearM, frame.LfWearR);
            _playerWearRfBeforePit = TelemetryMath.AverageWear(frame.RfWearL, frame.RfWearM, frame.RfWearR);
            _playerWearLrBeforePit = TelemetryMath.AverageWear(frame.LrWearL, frame.LrWearM, frame.LrWearR);
            _playerWearRrBeforePit = TelemetryMath.AverageWear(frame.RrWearL, frame.RrWearM, frame.RrWearR);
            _playerPitStartLap = Math.Max(0, frame.Lap);
            _playerLastPitSvFlags = frame.PitSvFlags;
            return null;
        }

        if (_playerOnPitRoad && frame.OnPitRoad)
        {
            if (frame.SpeedMetersPerSec < StationarySpeedThresholdMs || frame.PlayerTrackSurface == TrackSurface.InPitStall)
            {
                _playerPitStationaryS += dt;
            }

            if (frame.PitSvFlags != 0)
            {
                _playerLastPitSvFlags = frame.PitSvFlags;
            }

            return null;
        }

        if (_playerOnPitRoad && !frame.OnPitRoad)
        {
            _playerOnPitRoad = false;
            var pitlaneTimeS = Math.Max(0.0, frame.SessionTimeSeconds - _playerPitEntryTime);
            if (pitlaneTimeS < 2.0)
            {
                return null;
            }

            var fuelAdded = Math.Max(0.0, frame.FuelLevelLiters - _playerFuelBeforePit);

            // Reifenwechsel-Bitmaske: 1=LF, 2=RF, 4=LR, 8=RR (aus Wear-Reset oder PitSvFlags)
            var wearLfNow = TelemetryMath.AverageWear(frame.LfWearL, frame.LfWearM, frame.LfWearR);
            var wearRfNow = TelemetryMath.AverageWear(frame.RfWearL, frame.RfWearM, frame.RfWearR);
            var wearLrNow = TelemetryMath.AverageWear(frame.LrWearL, frame.LrWearM, frame.LrWearR);
            var wearRrNow = TelemetryMath.AverageWear(frame.RrWearL, frame.RrWearM, frame.RrWearR);

            var tiresMask = 0;
            if (wearLfNow - _playerWearLfBeforePit > 0.05) tiresMask |= 1;
            if (wearRfNow - _playerWearRfBeforePit > 0.05) tiresMask |= 2;
            if (wearLrNow - _playerWearLrBeforePit > 0.05) tiresMask |= 4;
            if (wearRrNow - _playerWearRrBeforePit > 0.05) tiresMask |= 8;

            if (tiresMask == 0 && (_playerLastPitSvFlags & 0x0F) != 0 && _playerPitStationaryS > 2.0)
            {
                tiresMask = _playerLastPitSvFlags & 0x0F;
            }

            if (tiresMask != 0)
            {
                _tireAgeLaps = 0;
            }

            return new PitstopTelemetryDto(
                LapNumber: _playerPitStartLap,
                PitlaneTimeS: Math.Round(pitlaneTimeS, 2),
                StationaryTimeS: Math.Round(_playerPitStationaryS, 2),
                FuelAddedL: Math.Round(fuelAdded, 2),
                TiresChangedMask: tiresMask,
                NewCompound: _metadata.ResolveTireCompound(frame.PlayerTireCompound));
        }

        return null;
    }

    private bool UpdateRaceField(TelemetryFrame frame, double dt)
    {
        var anyPitstopCompleted = false;

        foreach (var (carIdx, driver) in _metadata.DriversByCarIdx)
        {
            if (driver.IsPaceCar || driver.CustId <= 0)
            {
                continue;
            }

            if (carIdx >= frame.CarIdxLap.Length ||
                carIdx >= frame.CarIdxOnPitRoad.Length ||
                carIdx >= frame.CarIdxTireCompound.Length ||
                carIdx >= frame.CarIdxTrackSurface.Length)
            {
                continue;
            }

            var lap = frame.CarIdxLap[carIdx];
            var onPit = frame.CarIdxOnPitRoad[carIdx];
            var surface = (TrackSurface)frame.CarIdxTrackSurface[carIdx];
            var compound = _metadata.ResolveTireCompound(frame.CarIdxTireCompound[carIdx]);

            if (!_fieldDrivers.TryGetValue(driver.CustId, out var state))
            {
                state = new FieldDriverState
                {
                    CustId = driver.CustId,
                    CarNumber = driver.CarNumber,
                    CurrentCompound = compound
                };
                _fieldDrivers[driver.CustId] = state;
            }

            // Pitstop-Tracking pro Fahrer (vor Stint-Update, damit die Ausfahrtsrunde bereits zum neuen Stint zählt)
            if (!state.OnPitRoad && onPit)
            {
                state.OnPitRoad = true;
                state.PitEntryTime = frame.SessionTimeSeconds;
                state.StationaryTimeS = 0.0;
                state.PitStartLap = Math.Max(0, lap);
                state.CompoundBeforePit = state.CurrentCompound;
            }
            else if (state.OnPitRoad && onPit)
            {
                if (surface == TrackSurface.InPitStall)
                {
                    state.StationaryTimeS += dt;
                }
            }
            else if (state.OnPitRoad && !onPit)
            {
                state.OnPitRoad = false;
                var pitlaneTimeS = Math.Max(0.0, frame.SessionTimeSeconds - state.PitEntryTime);
                if (pitlaneTimeS >= 2.0 && state.PitStartLap > 0)
                {
                    var compoundAfter = !string.Equals(compound, "Unknown", StringComparison.Ordinal)
                        ? compound
                        : state.CompoundBeforePit;

                    state.Pitstops.Add(new RaceFieldPitstopDto(
                        LapNumber: state.PitStartLap,
                        PitlaneTimeS: Math.Round(pitlaneTimeS, 2),
                        StationaryTimeS: Math.Round(state.StationaryTimeS, 2),
                        CompoundBefore: state.CompoundBeforePit,
                        CompoundAfter: compoundAfter));

                    // Aktuellen Stint abschließen und neuen Stint starten
                    if (state.CurrentStintNumber > 0 && state.CurrentStintLastLap >= state.CurrentStintStartLap)
                    {
                        var stintEndLap = Math.Min(state.CurrentStintLastLap, state.PitStartLap);
                        state.CompletedStints.Add(new RaceFieldStintDto(
                            StintNumber: state.CurrentStintNumber,
                            StartLap: state.CurrentStintStartLap,
                            EndLap: stintEndLap,
                            LapsOnCompound: stintEndLap - state.CurrentStintStartLap + 1,
                            TireCompound: state.CurrentCompound));

                        state.CurrentStintNumber++;
                        state.CurrentStintStartLap = Math.Max(stintEndLap + 1, lap);
                        state.CurrentStintLastLap = state.CurrentStintStartLap;
                        state.CurrentCompound = compoundAfter;
                    }

                    anyPitstopCompleted = true;
                }
            }

            // Stint-Tracking
            if (lap > 0 && !onPit)
            {
                if (state.CurrentStintNumber == 0)
                {
                    state.CurrentStintNumber = 1;
                    state.CurrentStintStartLap = lap;
                    state.CurrentStintLastLap = lap;
                    state.CurrentCompound = compound;
                }
                else
                {
                    if (lap > state.CurrentStintLastLap)
                    {
                        state.CurrentStintLastLap = lap;
                    }

                    if (!string.Equals(compound, "Unknown", StringComparison.Ordinal) &&
                        string.Equals(state.CurrentCompound, "Unknown", StringComparison.Ordinal))
                    {
                        state.CurrentCompound = compound;
                    }
                }
            }
        }

        return anyPitstopCompleted;
    }

    private bool IsRaceSession(int sessionNum) =>
        _metadata.ResolveSessionType(sessionNum).Contains("Race", StringComparison.OrdinalIgnoreCase);

    private void ResetSessionState()
    {
        _currentLap = -1;
        _lapStartSessionTime = 0;
        _lapStartFuel = 0;
        _lapStartIncidents = 0;
        _lapWasInlap = false;
        _lapWasOutlap = false;
        _lapHadOfftrack = false;
        _tireAgeLaps = 0;
        _lastCompoundIndex = -1;
        _trace.Clear();

        _playerOnPitRoad = false;
        _playerPitEntryTime = 0;
        _playerPitStationaryS = 0;
        _fieldDrivers.Clear();
    }
}
