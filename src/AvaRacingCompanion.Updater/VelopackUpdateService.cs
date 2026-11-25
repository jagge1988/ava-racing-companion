using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Core.Versioning;
using Velopack;
using Velopack.Sources;

namespace AvaRacingCompanion.Updater;

/// <summary>
/// Velopack-basierter Updater gegen öffentliche GitHub Releases.
/// Im Modus <see cref="UpdateBehavior.NotifyOnly"/> werden ausschließlich
/// Release-Metadaten abgefragt – es erfolgt kein Download von Binärdateien.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService
{
    private readonly UpdateManager _updateManager;
    private readonly string _currentVersion;
    private readonly SemaphoreSlim _checkLock = new(1, 1);

    private UpdateInfo? _pendingUpdate;

    public VelopackUpdateService(string? repositoryUrl = null, string? currentVersion = null)
    {
        var repoUrl = string.IsNullOrWhiteSpace(repositoryUrl)
            ? AppInfo.GitHubRepositoryUrl
            : repositoryUrl;

        var source = new GithubSource(repoUrl, accessToken: null, prerelease: false);
        _updateManager = new UpdateManager(source);
        _currentVersion = currentVersion ?? AppInfo.GetCurrentVersion();
    }

    public bool IsInstalledViaVelopack => _updateManager.IsInstalled;

    public bool HasPendingUpdate => _pendingUpdate is not null;

    public string? PendingVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateBehavior behavior,
        Action<int>? onDownloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_updateManager.IsInstalled)
            {
                return new UpdateCheckResult(
                    Status: UpdateCheckStatus.NotInstalledViaVelopack,
                    CurrentVersion: _currentVersion,
                    ReleaseUrl: AppInfo.GitHubReleasesUrl,
                    Message: "Entwicklungs-Build (nicht über den Installer installiert). Updates sind erst in installierten Releases aktiv.");
            }

            var updateInfo = await _updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (updateInfo is null)
            {
                return new UpdateCheckResult(
                    Status: UpdateCheckStatus.UpToDate,
                    CurrentVersion: _currentVersion,
                    Message: $"Aktuell (v{_currentVersion}).");
            }

            var targetVersion = updateInfo.TargetFullRelease.Version.ToString();
            var releaseUrl = AppInfo.GetReleaseTagUrl(targetVersion);

            if (behavior == UpdateBehavior.NotifyOnly)
            {
                return new UpdateCheckResult(
                    Status: UpdateCheckStatus.UpdateAvailableNotifyOnly,
                    CurrentVersion: _currentVersion,
                    AvailableVersion: targetVersion,
                    ReleaseUrl: releaseUrl,
                    Message: $"Neue Version v{targetVersion} verfügbar (Auto-Update deaktiviert).");
            }

            if (_pendingUpdate is null ||
                !string.Equals(PendingVersion, targetVersion, StringComparison.OrdinalIgnoreCase))
            {
                await _updateManager.DownloadUpdatesAsync(updateInfo, onDownloadProgress).ConfigureAwait(false);
                _pendingUpdate = updateInfo;
            }

            return new UpdateCheckResult(
                Status: UpdateCheckStatus.UpdateDownloadedReadyToRestart,
                CurrentVersion: _currentVersion,
                AvailableVersion: targetVersion,
                ReleaseUrl: releaseUrl,
                Message: $"Version v{targetVersion} heruntergeladen und bereit zur Installation.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                Status: UpdateCheckStatus.Failed,
                CurrentVersion: _currentVersion,
                ReleaseUrl: AppInfo.GitHubReleasesUrl,
                Message: $"Update-Prüfung fehlgeschlagen: {ex.Message}");
        }
        finally
        {
            _checkLock.Release();
        }
    }

    public void ApplyPendingUpdateAndRestart()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        _updateManager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }

    public void ApplyPendingUpdateOnExit()
    {
        if (_pendingUpdate is null)
        {
            return;
        }

        _updateManager.WaitExitThenApplyUpdates(
            _pendingUpdate.TargetFullRelease,
            silent: true,
            restart: false);
    }
}
