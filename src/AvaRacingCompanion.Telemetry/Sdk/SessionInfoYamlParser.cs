using System.Globalization;
using System.Text.RegularExpressions;

namespace AvaRacingCompanion.Telemetry.Sdk;

public sealed record SessionDriverInfo(
    int CarIdx,
    int CustId,
    string UserName,
    string CarNumber,
    int CarId,
    bool IsPaceCar);

public sealed record SessionMetadata(
    int SubsessionId,
    int TrackId,
    string TrackDisplayName,
    double TrackLengthMeters,
    int PlayerCarIdx,
    int PlayerCarId,
    IReadOnlyDictionary<int, string> SessionTypesByNum,
    IReadOnlyDictionary<int, string> TireCompoundsByIndex,
    IReadOnlyDictionary<int, SessionDriverInfo> DriversByCarIdx)
{
    public string ResolveSessionType(int sessionNum) =>
        SessionTypesByNum.TryGetValue(sessionNum, out var type) ? type : "Unknown";

    public string ResolveTireCompound(int compoundIndex) =>
        compoundIndex >= 0 && TireCompoundsByIndex.TryGetValue(compoundIndex, out var name)
            ? name
            : (compoundIndex >= 0 ? $"Compound-{compoundIndex}" : "Unknown");

    public static SessionMetadata Empty() => SessionInfoYamlParser.Empty();
}

/// <summary>
/// Schlanker, abhängigkeitsfreier Parser für die relevanten Abschnitte des iRacing SessionInfo-YAML.
/// </summary>
public static partial class SessionInfoYamlParser
{
    [GeneratedRegex(@"^(\s*)-\s+(\w+):\s*(.*)$")]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"^(\s*)(\w+):\s*(.*)$")]
    private static partial Regex KeyValueRegex();

    public static SessionMetadata Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
        {
            return Empty();
        }

        var subsessionId = 0;
        var trackId = 0;
        var trackDisplayName = "Unbekannte Strecke";
        var trackLengthMeters = 0.0;
        var playerCarIdx = 0;

        var sessionTypes = new Dictionary<int, string>();
        var tireCompounds = new Dictionary<int, string>();
        var drivers = new Dictionary<int, SessionDriverInfo>();

        var section = string.Empty;
        var subSection = string.Empty;

        int? currentSessionNum = null;
        int? currentTireIndex = null;

        int? drvCarIdx = null;
        var drvCustId = 0;
        var drvUserName = string.Empty;
        var drvCarNumber = string.Empty;
        var drvCarId = 0;
        var drvIsPaceCar = false;

        void FlushDriver()
        {
            if (drvCarIdx.HasValue)
            {
                drivers[drvCarIdx.Value] = new SessionDriverInfo(
                    drvCarIdx.Value,
                    drvCustId,
                    drvUserName,
                    drvCarNumber,
                    drvCarId,
                    drvIsPaceCar);
            }

            drvCarIdx = null;
            drvCustId = 0;
            drvUserName = string.Empty;
            drvCarNumber = string.Empty;
            drvCarId = 0;
            drvIsPaceCar = false;
        }

        using var reader = new StringReader(yaml);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.TrimStart().StartsWith('#'))
            {
                continue;
            }

            // Top-Level-Abschnitt (keine Einrückung)
            if (!char.IsWhiteSpace(rawLine[0]) && rawLine.EndsWith(':'))
            {
                FlushDriver();
                section = rawLine.TrimEnd(':');
                subSection = string.Empty;
                currentSessionNum = null;
                currentTireIndex = null;
                continue;
            }

            var listMatch = ListItemRegex().Match(rawLine);
            if (listMatch.Success)
            {
                var key = listMatch.Groups[2].Value;
                var val = Unquote(listMatch.Groups[3].Value);

                if (section == "SessionInfo" && subSection == "Sessions" && key == "SessionNum")
                {
                    currentSessionNum = ParseInt(val);
                }
                else if (section == "DriverInfo" && subSection == "DriverTires" && key == "TireIndex")
                {
                    currentTireIndex = ParseInt(val);
                }
                else if (section == "DriverInfo" && subSection == "Drivers" && key == "CarIdx")
                {
                    FlushDriver();
                    drvCarIdx = ParseInt(val);
                }

                continue;
            }

            var kvMatch = KeyValueRegex().Match(rawLine);
            if (!kvMatch.Success)
            {
                continue;
            }

            var indent = kvMatch.Groups[1].Value.Length;
            var k = kvMatch.Groups[2].Value;
            var v = Unquote(kvMatch.Groups[3].Value);

            if (indent == 1 || indent == 2)
            {
                if (string.IsNullOrEmpty(v))
                {
                    FlushDriver();
                    subSection = k;
                    currentSessionNum = null;
                    currentTireIndex = null;
                    continue;
                }

                if (section == "WeekendInfo")
                {
                    switch (k)
                    {
                        case "SubSessionID": subsessionId = ParseInt(v); break;
                        case "TrackID": trackId = ParseInt(v); break;
                        case "TrackDisplayName": trackDisplayName = v; break;
                        case "TrackLength": trackLengthMeters = ParseTrackLengthMeters(v); break;
                    }
                }
                else if (section == "DriverInfo" && k == "DriverCarIdx")
                {
                    playerCarIdx = ParseInt(v);
                }
            }
            else if (section == "SessionInfo" && subSection == "Sessions" && currentSessionNum.HasValue && k == "SessionType")
            {
                sessionTypes[currentSessionNum.Value] = v;
            }
            else if (section == "DriverInfo" && subSection == "DriverTires" && currentTireIndex.HasValue && k == "TireCompoundType")
            {
                tireCompounds[currentTireIndex.Value] = v;
            }
            else if (section == "DriverInfo" && subSection == "Drivers" && drvCarIdx.HasValue)
            {
                switch (k)
                {
                    case "UserID": drvCustId = ParseInt(v); break;
                    case "UserName": drvUserName = v; break;
                    case "CarNumber": drvCarNumber = v; break;
                    case "CarID": drvCarId = ParseInt(v); break;
                    case "CarIsPaceCar": drvIsPaceCar = v == "1"; break;
                }
            }
        }

        FlushDriver();

        var playerCarId = drivers.TryGetValue(playerCarIdx, out var playerDriver)
            ? playerDriver.CarId
            : 0;

        return new SessionMetadata(
            subsessionId,
            trackId,
            trackDisplayName,
            trackLengthMeters,
            playerCarIdx,
            playerCarId,
            sessionTypes,
            tireCompounds,
            drivers);
    }

    public static SessionMetadata Empty() => new(
        0, 0, "Unbekannt", 0.0, 0, 0,
        new Dictionary<int, string>(),
        new Dictionary<int, string>(),
        new Dictionary<int, SessionDriverInfo>());

    private static int ParseInt(string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static double ParseTrackLengthMeters(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return 0.0;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            return 0.0;
        }

        if (parts.Length > 1 && parts[1].Equals("km", StringComparison.OrdinalIgnoreCase))
        {
            return Math.Round(num * 1000.0, 1);
        }

        return Math.Round(num, 1);
    }

    private static string Unquote(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
