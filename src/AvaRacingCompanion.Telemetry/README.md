# AvaRacingCompanion.Telemetry

60-Hz-Telemetrie- und Session-Modul für den AVA Racing Companion (0 externe NuGet-Pakete).

## Enthaltene Komponenten
- **Sdk (`IRacingMemoryMappedReader`, `SessionInfoYamlParser`, `TelemetryFrame`):** Rein lesender Zugriff auf das iRacing Shared-Memory SDK (`Local\IRSDKMemMapFileName`).
- **Tracking (`TelemetrySessionTracker`, `TelemetryMath`):**
  - Aktiver Fahrer (Practice, Quali & Race): Rundenzeiten, exakter Spritverbrauch, Reifensatz & -verschleiß (`LF/RF/LR/RR`), Boxenstopp-Standzeit, getankte Liter, Reifenwechsel-Bitmaske sowie volle **60-Hz-Traces** inkl. berechnetem **Slip Angle** ($\operatorname{atan2}(v_y, v_x)$).
  - Race-Sessions zusätzlich: Feld-Tracking aller Fahrer (`CarIdxTireCompound`, `CarIdxOnPitRoad`, `CarIdxTrackSurface == InPitStall`) für `/pitstops`.
- **Api (`TelemetryApiClient`, `OfflineUploadOutbox`):** HTTP-v1-Client inkl. `gzip`-Kompression für Traces und lokaler Offline-Retry-Queue in `%APPDATA%\AvaRacingCompanion\outbox\`.
