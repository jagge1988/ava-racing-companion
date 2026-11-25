namespace AvaRacingCompanion.Telemetry.Tracking;

public static class TelemetryMath
{
    private const double MinSpeedMetersPerSecForSlip = 3.0;

    /// <summary>
    /// Berechnet den Fahrzeug-Schwimmwinkel (Body Slip Angle) in Grad aus VelocityX / VelocityY.
    /// Unterhalb von 3 m/s wird 0 zurückgegeben, um Rauschen im Stand zu vermeiden.
    /// </summary>
    public static double ComputeSlipAngleDeg(double velocityX, double velocityY)
    {
        var speedSq = (velocityX * velocityX) + (velocityY * velocityY);
        if (speedSq < MinSpeedMetersPerSecForSlip * MinSpeedMetersPerSecForSlip)
        {
            return 0.0;
        }

        var angleRad = Math.Atan2(velocityY, Math.Abs(velocityX) < 1e-6 ? 1e-6 : velocityX);
        var deg = angleRad * (180.0 / Math.PI);
        return Math.Round(Math.Clamp(deg, -90.0, 90.0), 2);
    }

    public static double AverageWear(double left, double middle, double right)
    {
        var avg = (left + middle + right) / 3.0;
        return Math.Round(Math.Clamp(avg, 0.0, 1.0), 4);
    }
}
