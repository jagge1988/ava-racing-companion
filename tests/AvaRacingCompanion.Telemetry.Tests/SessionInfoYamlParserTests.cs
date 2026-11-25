using AvaRacingCompanion.Telemetry.Sdk;
using Xunit;

namespace AvaRacingCompanion.Telemetry.Tests;

public sealed class SessionInfoYamlParserTests
{
    private const string SampleYaml = """
---
WeekendInfo:
 TrackName: spa up
 TrackID: 163
 TrackLength: 7.00 km
 TrackDisplayName: Circuit de Spa-Francorchamps
 SubSessionID: 98765432

SessionInfo:
 Sessions:
 - SessionNum: 0
   SessionType: Practice
 - SessionNum: 1
   SessionType: Lone Qualify
 - SessionNum: 2
   SessionType: Race

DriverInfo:
 DriverCarIdx: 1
 DriverTires:
 - TireIndex: 0
   TireCompoundType: Hard
 - TireIndex: 1
   TireCompoundType: Medium
 - TireIndex: 2
   TireCompoundType: Soft
 Drivers:
 - CarIdx: 0
   UserName: Pace Car
   UserID: -1
   CarNumber: "0"
   CarID: 100
   CarIsPaceCar: 1
 - CarIdx: 1
   UserName: Martin Fleth
   UserID: 123456
   CarNumber: "42"
   CarID: 173
   CarIsPaceCar: 0
 - CarIdx: 2
   UserName: Team Rival
   UserID: 654321
   CarNumber: "7"
   CarID: 173
   CarIsPaceCar: 0
...
""";

    [Fact]
    public void Parse_ExtractsWeekendSessionsTiresAndDrivers()
    {
        var meta = SessionInfoYamlParser.Parse(SampleYaml);

        Assert.Equal(98765432, meta.SubsessionId);
        Assert.Equal(163, meta.TrackId);
        Assert.Equal("Circuit de Spa-Francorchamps", meta.TrackDisplayName);
        Assert.Equal(7000.0, meta.TrackLengthMeters);
        Assert.Equal(1, meta.PlayerCarIdx);
        Assert.Equal(173, meta.PlayerCarId);

        Assert.Equal("Practice", meta.ResolveSessionType(0));
        Assert.Equal("Race", meta.ResolveSessionType(2));

        Assert.Equal("Hard", meta.ResolveTireCompound(0));
        Assert.Equal("Medium", meta.ResolveTireCompound(1));
        Assert.Equal("Soft", meta.ResolveTireCompound(2));

        Assert.Equal(3, meta.DriversByCarIdx.Count);
        Assert.True(meta.DriversByCarIdx[0].IsPaceCar);
        Assert.Equal(123456, meta.DriversByCarIdx[1].CustId);
        Assert.Equal("42", meta.DriversByCarIdx[1].CarNumber);
        Assert.Equal(654321, meta.DriversByCarIdx[2].CustId);
    }
}
