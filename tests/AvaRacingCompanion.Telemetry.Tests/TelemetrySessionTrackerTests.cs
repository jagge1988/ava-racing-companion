using AvaRacingCompanion.Core.Telemetry;
using AvaRacingCompanion.Telemetry.Sdk;
using AvaRacingCompanion.Telemetry.Tracking;
using Xunit;

namespace AvaRacingCompanion.Telemetry.Tests;

public sealed class TelemetrySessionTrackerTests
{
    private static SessionMetadata CreateMetadata() => new(
        SubsessionId: 555001,
        TrackId: 163,
        TrackDisplayName: "Spa",
        TrackLengthMeters: 7004.0,
        PlayerCarIdx: 1,
        PlayerCarId: 173,
        SessionTypesByNum: new Dictionary<int, string>
        {
            [0] = "Practice",
            [2] = "Race"
        },
        TireCompoundsByIndex: new Dictionary<int, string>
        {
            [0] = "Hard",
            [1] = "Medium"
        },
        DriversByCarIdx: new Dictionary<int, SessionDriverInfo>
        {
            [0] = new(0, -1, "Pace Car", "0", 100, true),
            [1] = new(1, 123456, "Martin", "42", 173, false),
            [2] = new(2, 654321, "Rival", "7", 173, false)
        });

    [Fact]
    public void PracticeSession_EmitsActiveDriverLapAnd60HzTrace_WithoutRaceField()
    {
        var tracker = new TelemetrySessionTracker { UploadTracesEnabled = true };
        tracker.UpdateMetadata(CreateMetadata());

        // 120 Ticks (2 Sekunden bei 60 Hz) in Runde 1
        for (var i = 0; i < 120; i++)
        {
            var t = 100.0 + (i / 60.0);
            var outp = tracker.ProcessFrame(new TelemetryFrame
            {
                TickCount = i,
                SessionTimeSeconds = t,
                SessionNum = 0, // Practice
                Lap = 1,
                LapDistMeters = i * 2.5f,
                Lat = 50.4372,
                Lon = 5.9714,
                SpeedMetersPerSec = 50f,
                Throttle = 1.0f,
                Brake = i < 20 ? 0.8f : 0f,
                SteeringWheelAngleRad = 0.15f,
                VelocityX = 50f,
                VelocityY = 2.5f,
                Gear = 4,
                Rpm = 7200f,
                FuelLevelLiters = 50.0f - (i * 0.02f),
                TrackTempCelsius = 29.5f,
                PlayerTireCompound = 1, // Medium
                LfWearL = 0.96f, LfWearM = 0.95f, LfWearR = 0.94f,
                BrakeAbsActive = i < 10,
                DrsStatus = i >= 100 ? 3 : 0,
                ErsBatteryPct = 82.5f
            });

            Assert.Null(outp.LapsUpload);
            Assert.Null(outp.RaceFieldUpload);
        }

        // Übergang auf Runde 2 löst Lap + 60-Hz-Trace aus (gemessene Dauer 2,0 s; SDK meldet 2,010 s)
        var completed = tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 120,
            SessionTimeSeconds = 102.0,
            SessionNum = 0,
            Lap = 2,
            LapLastLapTimeSeconds = 2.010f,
            FuelLevelLiters = 47.0f,
            TrackTempCelsius = 29.5f,
            PlayerTireCompound = 1,
            LfWearL = 0.93f, LfWearM = 0.92f, LfWearR = 0.91f
        });

        Assert.NotNull(completed.LapsUpload);
        Assert.Null(completed.RaceFieldUpload);
        var lap = Assert.Single(completed.LapsUpload.Laps);
        Assert.Equal(1, lap.LapNumber);
        Assert.Equal(2010, lap.LapTimeMs);
        Assert.Equal(3.0, lap.FuelUsedL, precision: 2);
        Assert.Equal("Medium", lap.TireCompound);
        Assert.Equal(1, lap.TireAgeLaps);
        Assert.Equal(0.92, lap.WearLf, precision: 2);
        Assert.True(lap.IsClean);

        Assert.NotNull(completed.TraceUpload);
        Assert.Equal(60, completed.TraceUpload.SampleRateHz);
        Assert.Equal(120, completed.TraceUpload.TimeMs.Count);
        Assert.Equal(120, completed.TraceUpload.LapDistM.Count);
        Assert.Equal(120, completed.TraceUpload.SlipAngleDeg.Count);
        Assert.True(completed.TraceUpload.SlipAngleDeg[0] > 2.0);
        Assert.True((completed.TraceUpload.Flags[0] & (byte)TelemetryFlags.Abs) != 0);
        Assert.True((completed.TraceUpload.Flags[110] & (byte)TelemetryFlags.Drs) != 0);
    }

    [Fact]
    public void RaceSession_TracksAllDriversPitstopsAndStintsForRaceField()
    {
        var tracker = new TelemetrySessionTracker { UploadTracesEnabled = false };
        tracker.UpdateMetadata(CreateMetadata());

        // Rival (CarIdx 2, CustId 654321) fährt Runden 1..10 auf Medium
        for (var lap = 1; lap <= 10; lap++)
        {
            tracker.ProcessFrame(new TelemetryFrame
            {
                TickCount = lap,
                SessionTimeSeconds = lap * 100.0,
                SessionNum = 2, // Race
                Lap = lap,
                CarIdxLap = new[] { 0, lap, lap },
                CarIdxTireCompound = new[] { -1, 1, 1 }, // Medium
                CarIdxOnPitRoad = new[] { false, false, false },
                CarIdxTrackSurface = new[] { 3, 3, 3 }
            });
        }

        // Rival fährt in Runde 10 in die Box (40s Pitlane, davon 20s InPitStall) und wechselt auf Hard (0)
        tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 11,
            SessionTimeSeconds = 1010.0,
            SessionNum = 2,
            Lap = 10,
            CarIdxLap = new[] { 0, 10, 10 },
            CarIdxTireCompound = new[] { -1, 1, 1 },
            CarIdxOnPitRoad = new[] { false, false, true },
            CarIdxTrackSurface = new[] { 3, 3, (int)TrackSurface.ApproachingPits }
        });

        // 20 Sekunden in der Boxenbox (InPitStall) in 0.5s-Schritten
        for (var step = 1; step <= 40; step++)
        {
            tracker.ProcessFrame(new TelemetryFrame
            {
                TickCount = 11 + step,
                SessionTimeSeconds = 1010.0 + (step * 0.5),
                SessionNum = 2,
                Lap = 10,
                CarIdxLap = new[] { 0, 10, 10 },
                CarIdxTireCompound = new[] { -1, 1, 0 }, // Wechsel auf Hard
                CarIdxOnPitRoad = new[] { false, false, true },
                CarIdxTrackSurface = new[] { 3, 3, (int)TrackSurface.InPitStall }
            });
        }

        // Ausfahrt aus der Boxengasse
        var afterPit = tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 100,
            SessionTimeSeconds = 1050.0,
            SessionNum = 2,
            Lap = 11,
            CarIdxLap = new[] { 0, 11, 11 },
            CarIdxTireCompound = new[] { -1, 1, 0 }, // Hard
            CarIdxOnPitRoad = new[] { false, false, false },
            CarIdxTrackSurface = new[] { 3, 3, (int)TrackSurface.OnTrack }
        });

        Assert.NotNull(afterPit.RaceFieldUpload);
        var rival = Assert.Single(afterPit.RaceFieldUpload.Drivers, d => d.CustId == 654321);
        Assert.Equal("7", rival.CarNumber);

        var pit = Assert.Single(rival.Pitstops);
        Assert.Equal(10, pit.LapNumber);
        Assert.Equal(40.0, pit.PitlaneTimeS, precision: 1);
        Assert.Equal(20.0, pit.StationaryTimeS, precision: 1);
        Assert.Equal("Medium", pit.CompoundBefore);
        Assert.Equal("Hard", pit.CompoundAfter);

        Assert.Equal(2, rival.Stints.Count);
        Assert.Equal("Medium", rival.Stints[0].TireCompound);
        Assert.Equal(10, rival.Stints[0].LapsOnCompound);
        Assert.Equal("Hard", rival.Stints[1].TireCompound);
    }

    [Fact]
    public void ComputeSlipAngleDeg_ReturnsZeroBelowThresholdAndAngleAtSpeed()
    {
        Assert.Equal(0.0, TelemetryMath.ComputeSlipAngleDeg(1.0, 0.5));
        Assert.InRange(TelemetryMath.ComputeSlipAngleDeg(40.0, 4.0), 5.5, 6.0);
    }

    [Fact]
    public void CompleteLap_WhenSdkLapLastLapTimeLagsBehind_UsesMeasuredDurationWithoutOffByOne()
    {
        var tracker = new TelemetrySessionTracker { UploadTracesEnabled = true };
        tracker.UpdateMetadata(CreateMetadata());

        // Runde 7 dauert exakt 110,0 s; währenddessen steht LapLastLapTime noch auf Runde 6 (90,5 s)
        for (var i = 0; i < 66; i++)
        {
            tracker.ProcessFrame(new TelemetryFrame
            {
                TickCount = i,
                SessionTimeSeconds = 100.0 + (i * (110.0 / 66.0)),
                SessionNum = 2,
                Lap = 7,
                LapLastLapTimeSeconds = 90.500f,
                FuelLevelLiters = 40f
            });
        }

        // Übergang auf Runde 8: SDK-Lag! LapLastLapTime zeigt noch immer 90,500 s (Runde 6)
        var lap7Done = tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 66,
            SessionTimeSeconds = 210.0,
            SessionNum = 2,
            Lap = 8,
            LapLastLapTimeSeconds = 90.500f,
            FuelLevelLiters = 37f
        });

        Assert.NotNull(lap7Done.LapsUpload);
        var lap7 = Assert.Single(lap7Done.LapsUpload.Laps);
        Assert.Equal(7, lap7.LapNumber);
        Assert.InRange(lap7.LapTimeMs, 109900, 110100);

        // 1 Tick später aktualisiert iRacing LapLastLapTime auf Runde 7 (110,0 s), während Runde 8 (91,0 s) läuft
        for (var i = 1; i < 66; i++)
        {
            tracker.ProcessFrame(new TelemetryFrame
            {
                TickCount = 66 + i,
                SessionTimeSeconds = 210.0 + (i * (91.0 / 66.0)),
                SessionNum = 2,
                Lap = 8,
                LapLastLapTimeSeconds = 110.000f,
                FuelLevelLiters = 36f
            });
        }

        // Übergang auf Runde 9: SDK-Lag! LapLastLapTime zeigt noch 110,000 s (Runde 7)
        var lap8Done = tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 200,
            SessionTimeSeconds = 301.0,
            SessionNum = 2,
            Lap = 9,
            LapLastLapTimeSeconds = 110.000f,
            FuelLevelLiters = 34f
        });

        Assert.NotNull(lap8Done.LapsUpload);
        var lap8 = Assert.Single(lap8Done.LapsUpload.Laps);
        Assert.Equal(8, lap8.LapNumber);
        Assert.InRange(lap8.LapTimeMs, 90900, 91100);
    }

    [Fact]
    public void PitstopOnLapZero_AtSessionStart_IsFilteredOut()
    {
        var tracker = new TelemetrySessionTracker { UploadTracesEnabled = false };
        tracker.UpdateMetadata(CreateMetadata());

        // Fahrer startet in der Box auf Lap 0 und fährt nach 15 Sekunden raus
        tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 1,
            SessionTimeSeconds = 10.0,
            SessionNum = 0,
            Lap = 0,
            OnPitRoad = true,
            PlayerTrackSurface = TrackSurface.InPitStall,
            FuelLevelLiters = 50f
        });

        tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 2,
            SessionTimeSeconds = 20.0,
            SessionNum = 0,
            Lap = 0,
            OnPitRoad = true,
            PlayerTrackSurface = TrackSurface.InPitStall,
            FuelLevelLiters = 50f
        });

        var exit = tracker.ProcessFrame(new TelemetryFrame
        {
            TickCount = 3,
            SessionTimeSeconds = 25.0,
            SessionNum = 0,
            Lap = 0,
            OnPitRoad = false,
            PlayerTrackSurface = TrackSurface.OnTrack,
            FuelLevelLiters = 50f
        });

        Assert.Null(exit.PitstopsUpload);
    }
}
