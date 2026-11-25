namespace AvaRacingCompanion.Updater;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailableNotifyOnly,
    UpdateDownloadedReadyToRestart,
    NotInstalledViaVelopack,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? AvailableVersion = null,
    string? ReleaseUrl = null,
    string? Message = null);
