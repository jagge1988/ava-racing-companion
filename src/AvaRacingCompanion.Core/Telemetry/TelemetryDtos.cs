using System.Text.Json.Serialization;

namespace AvaRacingCompanion.Core.Telemetry;

[Flags]
public enum TelemetryFlags : byte
{
    None = 0,
    Drs = 1,
    Abs = 2,
    Tc = 4,
    Offtrack = 8,
    Pit = 16
}

public sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("api_version")] int ApiVersion);

public sealed record PairExchangeRequest(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("device_name")] string DeviceName);

public sealed record PairExchangeResponse(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("cust_id")] int CustId,
    [property: JsonPropertyName("driver_name")] string DriverName);

public sealed record LapTelemetryDto(
    [property: JsonPropertyName("lap_number")] int LapNumber,
    [property: JsonPropertyName("lap_time_ms")] int LapTimeMs,
    [property: JsonPropertyName("fuel_start_l")] double FuelStartL,
    [property: JsonPropertyName("fuel_end_l")] double FuelEndL,
    [property: JsonPropertyName("fuel_used_l")] double FuelUsedL,
    [property: JsonPropertyName("tire_compound")] string TireCompound,
    [property: JsonPropertyName("tire_age_laps")] int TireAgeLaps,
    [property: JsonPropertyName("wear_lf")] double WearLf,
    [property: JsonPropertyName("wear_rf")] double WearRf,
    [property: JsonPropertyName("wear_lr")] double WearLr,
    [property: JsonPropertyName("wear_rr")] double WearRr,
    [property: JsonPropertyName("track_temp_c")] double TrackTempC,
    [property: JsonPropertyName("incidents_delta")] int IncidentsDelta,
    [property: JsonPropertyName("is_clean")] bool IsClean,
    [property: JsonPropertyName("is_inlap")] bool IsInlap,
    [property: JsonPropertyName("is_outlap")] bool IsOutlap);

public sealed record LapsUploadRequest(
    [property: JsonPropertyName("subsession_id")] int SubsessionId,
    [property: JsonPropertyName("session_num")] int SessionNum,
    [property: JsonPropertyName("session_type")] string SessionType,
    [property: JsonPropertyName("track_id")] int TrackId,
    [property: JsonPropertyName("car_id")] int CarId,
    [property: JsonPropertyName("laps")] IReadOnlyList<LapTelemetryDto> Laps);

public sealed record PitstopTelemetryDto(
    [property: JsonPropertyName("lap_number")] int LapNumber,
    [property: JsonPropertyName("pitlane_time_s")] double PitlaneTimeS,
    [property: JsonPropertyName("stationary_time_s")] double StationaryTimeS,
    [property: JsonPropertyName("fuel_added_l")] double FuelAddedL,
    [property: JsonPropertyName("tires_changed_mask")] int TiresChangedMask,
    [property: JsonPropertyName("new_compound")] string NewCompound);

public sealed record PitstopsUploadRequest(
    [property: JsonPropertyName("subsession_id")] int SubsessionId,
    [property: JsonPropertyName("pitstops")] IReadOnlyList<PitstopTelemetryDto> Pitstops);

public sealed record RaceFieldStintDto(
    [property: JsonPropertyName("stint_number")] int StintNumber,
    [property: JsonPropertyName("start_lap")] int StartLap,
    [property: JsonPropertyName("end_lap")] int EndLap,
    [property: JsonPropertyName("laps_on_compound")] int LapsOnCompound,
    [property: JsonPropertyName("tire_compound")] string TireCompound);

public sealed record RaceFieldPitstopDto(
    [property: JsonPropertyName("lap_number")] int LapNumber,
    [property: JsonPropertyName("pitlane_time_s")] double PitlaneTimeS,
    [property: JsonPropertyName("stationary_time_s")] double StationaryTimeS,
    [property: JsonPropertyName("compound_before")] string CompoundBefore,
    [property: JsonPropertyName("compound_after")] string CompoundAfter);

public sealed record RaceFieldDriverDto(
    [property: JsonPropertyName("cust_id")] int CustId,
    [property: JsonPropertyName("car_number")] string CarNumber,
    [property: JsonPropertyName("stints")] IReadOnlyList<RaceFieldStintDto> Stints,
    [property: JsonPropertyName("pitstops")] IReadOnlyList<RaceFieldPitstopDto> Pitstops);

public sealed record RaceFieldUploadRequest(
    [property: JsonPropertyName("subsession_id")] int SubsessionId,
    [property: JsonPropertyName("drivers")] IReadOnlyList<RaceFieldDriverDto> Drivers);

public sealed record LapTraceUploadRequest(
    [property: JsonPropertyName("sample_rate_hz")] int SampleRateHz,
    [property: JsonPropertyName("subsession_id")] int SubsessionId,
    [property: JsonPropertyName("lap_number")] int LapNumber,
    [property: JsonPropertyName("track_length_m")] double TrackLengthM,
    [property: JsonPropertyName("time_ms")] IReadOnlyList<int> TimeMs,
    [property: JsonPropertyName("lap_dist_m")] IReadOnlyList<double> LapDistM,
    [property: JsonPropertyName("lat")] IReadOnlyList<double> Lat,
    [property: JsonPropertyName("lon")] IReadOnlyList<double> Lon,
    [property: JsonPropertyName("speed_kmh")] IReadOnlyList<double> SpeedKmh,
    [property: JsonPropertyName("throttle")] IReadOnlyList<double> Throttle,
    [property: JsonPropertyName("brake")] IReadOnlyList<double> Brake,
    [property: JsonPropertyName("steering_rad")] IReadOnlyList<double> SteeringRad,
    [property: JsonPropertyName("slip_angle_deg")] IReadOnlyList<double> SlipAngleDeg,
    [property: JsonPropertyName("gear")] IReadOnlyList<int> Gear,
    [property: JsonPropertyName("rpm")] IReadOnlyList<int> Rpm,
    [property: JsonPropertyName("ers_pct")] IReadOnlyList<double> ErsPct,
    [property: JsonPropertyName("flags")] IReadOnlyList<byte> Flags);
