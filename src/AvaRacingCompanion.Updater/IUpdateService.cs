using AvaRacingCompanion.Core.Config;

namespace AvaRacingCompanion.Updater;

/// <summary>
/// Einheitliche Schnittstelle für Update-Prüfungen gegen GitHub Releases.
/// </summary>
public interface IUpdateService
{
    bool IsInstalledViaVelopack { get; }
    bool HasPendingUpdate { get; }
    string? PendingVersion { get; }

    Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateBehavior behavior,
        Action<int>? onDownloadProgress = null,
        CancellationToken cancellationToken = default);

    void ApplyPendingUpdateAndRestart();
    void ApplyPendingUpdateOnExit();
}
