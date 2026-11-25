using System.Text;
using AvaRacingCompanion.Telemetry.Api;
using AvaRacingCompanion.Telemetry.Sdk;
using Xunit;

namespace AvaRacingCompanion.Telemetry.Tests;

public sealed class IbtFileReaderAndSyncTests : IDisposable
{
    private readonly string _tempDir;

    public IbtFileReaderAndSyncTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ava-ibt-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void IbtFileReader_ReadsBinaryIbtHeaderMetadataAnd60HzFrames()
    {
        var ibtBytes = BuildSyntheticIbtFile(recordCount: 65);
        using var ms = new MemoryStream(ibtBytes);

        var (meta, frames) = IbtFileReader.ReadAll(ms);

        Assert.Equal(777888, meta.SubsessionId);
        Assert.Equal(163, meta.TrackId);
        Assert.Equal(7004.0, meta.TrackLengthMeters);
        Assert.Equal(65, frames.Count);
        Assert.True(frames[0].IsOnTrack);
        Assert.Equal(1, frames[0].Lap);
        Assert.Equal(2, frames[64].Lap);
        Assert.Equal(50.0f, frames[0].SpeedMetersPerSec, precision: 2);
    }

    [Fact]
    public void IbtSyncService_ProcessesNewIbtOnceAndSkipsOnRescan()
    {
        var ibtDir = Path.Combine(_tempDir, "telemetry");
        var outboxDir = Path.Combine(_tempDir, "outbox");
        var stateFile = Path.Combine(_tempDir, "processed_ibt.json");
        Directory.CreateDirectory(ibtDir);

        var ibtPath = Path.Combine(ibtDir, "gt3_spa_2026-11-25.ibt");
        File.WriteAllBytes(ibtPath, BuildSyntheticIbtFile(recordCount: 65));

        var outbox = new OfflineUploadOutbox(outboxDir);
        var syncService = new IbtSyncService(stateFile, minFileAge: TimeSpan.Zero);

        var firstScan = syncService.ScanAndEnqueueNewFiles(ibtDir, outbox, uploadTracesEnabled: true);
        Assert.Equal(1, firstScan.FilesProcessed);
        Assert.Equal(2, firstScan.PayloadsEnqueued); // 1x laps + 1x 60Hz trace
        Assert.Equal(2, outbox.PendingCount);

        // Zweiter Scan darf dieselbe Datei nicht erneut verarbeiten
        var secondScan = syncService.ScanAndEnqueueNewFiles(ibtDir, outbox, uploadTracesEnabled: true);
        Assert.Equal(0, secondScan.FilesProcessed);
        Assert.Equal(0, secondScan.PayloadsEnqueued);
        Assert.Equal(2, outbox.PendingCount);
    }

    private static byte[] BuildSyntheticIbtFile(int recordCount)
    {
        var yaml = """
---
WeekendInfo:
 SubSessionID: 777888
 TrackID: 163
 TrackDisplayName: Spa
 TrackLength: 7.004 km
SessionInfo:
 Sessions:
 - SessionNum: 0
   SessionType: Practice
DriverInfo:
 DriverCarIdx: 0
 DriverTires:
 - TireIndex: 0
   TireCompoundType: Dry
 Drivers:
 - CarIdx: 0
   UserName: Martin
   UserID: 123456
   CarNumber: "42"
   CarID: 173
   CarIsPaceCar: 0
...
""";
        var yamlBytes = Encoding.Latin1.GetBytes(yaml + "\0");

        // Variablen-Layout im Sample-Puffer:
        // 0: IsOnTrack (bool, type 1)
        // 4: SessionTime (double, type 5)
        // 12: SessionNum (int, type 2)
        // 16: Lap (int, type 2)
        // 20: LapLastLapTime (float, type 4)
        // 24: LapDist (float, type 4)
        // 28: Speed (float, type 4)
        // 32: FuelLevel (float, type 4)
        // 36: PlayerTireCompound (int, type 2)
        // 40: VelocityX (float, type 4)
        // 44: VelocityY (float, type 4)
        var vars = new (string Name, int Type, int Offset)[]
        {
            ("IsOnTrack", 1, 0),
            ("SessionTime", 5, 4),
            ("SessionNum", 2, 12),
            ("Lap", 2, 16),
            ("LapLastLapTime", 4, 20),
            ("LapDist", 4, 24),
            ("Speed", 4, 28),
            ("FuelLevel", 4, 32),
            ("PlayerTireCompound", 2, 36),
            ("VelocityX", 4, 40),
            ("VelocityY", 4, 44)
        };

        const int bufLen = 48;
        const int headerSize = 144; // 112 + 32
        var varHeaderOffset = headerSize;
        var sessionInfoOffset = varHeaderOffset + (vars.Length * IRacingVarTable.VarHeaderSize);
        var dataStartOffset = sessionInfoOffset + yamlBytes.Length;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        // 112-Byte irsdk_header
        bw.Write(2); // ver
        bw.Write(1); // status
        bw.Write(60); // tickRate
        bw.Write(1); // sessionInfoUpdate
        bw.Write(yamlBytes.Length); // sessionInfoLen
        bw.Write(sessionInfoOffset); // sessionInfoOffset
        bw.Write(vars.Length); // numVars
        bw.Write(varHeaderOffset); // varHeaderOffset
        bw.Write(1); // numBuf
        bw.Write(bufLen); // bufLen
        bw.Write(0); bw.Write(0); // pad1
        // varBuf[0]
        bw.Write(0); // tickCount
        bw.Write(dataStartOffset); // bufOffset (at byte 52)
        bw.Write(0); bw.Write(0); // pad
        // varBuf[1..3] (3 * 16 = 48 Bytes)
        bw.Write(new byte[48]);

        // 32-Byte irsdk_diskSubHeader (Offset 112..143)
        bw.Write(0L); // sessionStartDate
        bw.Write(0.0); // sessionStartTime
        bw.Write(120.0); // sessionEndTime
        bw.Write(2); // sessionLapCount (Offset 136)
        bw.Write(recordCount); // sessionRecordCount (Offset 140)

        // Variable Headers
        foreach (var (name, type, offset) in vars)
        {
            bw.Write(type);
            bw.Write(offset);
            bw.Write(1); // count
            bw.Write(0); // countAsTime + pad
            var nameBuf = new byte[32];
            Encoding.ASCII.GetBytes(name, 0, name.Length, nameBuf, 0);
            bw.Write(nameBuf);
            bw.Write(new byte[64]); // desc
            bw.Write(new byte[32]); // unit
        }

        // SessionInfo YAML
        bw.Write(yamlBytes);

        // 60-Hz Sample-Records
        for (var i = 0; i < recordCount; i++)
        {
            var sample = new byte[bufLen];
            sample[0] = 1; // IsOnTrack = true
            BitConverter.TryWriteBytes(sample.AsSpan(4, 8), 10.0 + (i / 60.0));
            BitConverter.TryWriteBytes(sample.AsSpan(12, 4), 0); // Practice
            BitConverter.TryWriteBytes(sample.AsSpan(16, 4), i < recordCount - 1 ? 1 : 2); // Lap 1 -> 2
            BitConverter.TryWriteBytes(sample.AsSpan(20, 4), i < recordCount - 1 ? 0f : 135.25f);
            BitConverter.TryWriteBytes(sample.AsSpan(24, 4), i * 1.5f);
            BitConverter.TryWriteBytes(sample.AsSpan(28, 4), 50.0f);
            BitConverter.TryWriteBytes(sample.AsSpan(32, 4), 40.0f - (i * 0.04f));
            BitConverter.TryWriteBytes(sample.AsSpan(36, 4), 0); // Dry
            BitConverter.TryWriteBytes(sample.AsSpan(40, 4), 50.0f);
            BitConverter.TryWriteBytes(sample.AsSpan(44, 4), 1.5f);
            bw.Write(sample);
        }

        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }
}
