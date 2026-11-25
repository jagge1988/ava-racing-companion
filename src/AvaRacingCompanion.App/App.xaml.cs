using System.Windows;
using System.Windows.Threading;
using AvaRacingCompanion.App.Services;
using AvaRacingCompanion.App.Tray;
using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Core.Services;
using AvaRacingCompanion.Updater;
using WpfApplication = System.Windows.Application;

namespace AvaRacingCompanion.App;

public partial class App : WpfApplication
{
    private readonly bool _startMinimized;
    private readonly SettingsStore _settingsStore = new();
    private readonly IAutostartService _autostartService = new WindowsAutostartService();
    private readonly IUpdateService _updateService = new VelopackUpdateService();

    private AppSettings _settings = new();
    private TrayIconManager? _trayIcon;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _updateTimer;
    private bool _isExiting;

    public App(bool startMinimized = false)
    {
        _startMinimized = startMinimized;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _settings = _settingsStore.Load();

        // Registry-Status mit gespeicherter Einstellung abgleichen
        var autostartActual = _autostartService.IsEnabled();
        if (autostartActual != _settings.LaunchAtStartup)
        {
            _settings = _settings with { LaunchAtStartup = autostartActual };
            _settingsStore.Save(_settings);
        }

        _trayIcon = new TrayIconManager();
        _trayIcon.OpenRequested += (_, _) => ShowMainWindow();
        _trayIcon.CheckForUpdatesRequested += async (_, _) => await RunUpdateCheckAsync(isManual: true);
        _trayIcon.ExitRequested += (_, _) => ExitApplication();

        _mainWindow = new MainWindow(
            initialSettings: _settings,
            onSettingsChanged: HandleSettingsChanged,
            onManualUpdateCheckRequested: () => RunUpdateCheckAsync(isManual: true),
            onApplyUpdateNowRequested: ApplyUpdateAndRestart);

        if (!_startMinimized)
        {
            ShowMainWindow();
        }

        StartPeriodicUpdateChecks();
        _ = RunStartupUpdateCheckAsync();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
    }

    private void HandleSettingsChanged(AppSettings updatedSettings)
    {
        var previousAutostart = _settings.LaunchAtStartup;
        _settings = updatedSettings.Normalized();
        _settingsStore.Save(_settings);

        if (previousAutostart != _settings.LaunchAtStartup)
        {
            _autostartService.SetEnabled(_settings.LaunchAtStartup);
        }

        if (_updateTimer is not null)
        {
            _updateTimer.Interval = TimeSpan.FromMinutes(_settings.UpdateCheckIntervalMinutes);
        }
    }

    private void StartPeriodicUpdateChecks()
    {
        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(_settings.UpdateCheckIntervalMinutes)
        };
        _updateTimer.Tick += async (_, _) => await RunUpdateCheckAsync(isManual: false);
        _updateTimer.Start();
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (!_isExiting)
        {
            await RunUpdateCheckAsync(isManual: false);
        }
    }

    private async Task RunUpdateCheckAsync(bool isManual)
    {
        _mainWindow?.SetUpdateCheckingState();

        var result = await _updateService.CheckForUpdatesAsync(
            _settings.UpdateBehavior,
            onDownloadProgress: progress =>
            {
                Dispatcher.Invoke(() => _mainWindow?.SetDownloadProgress(progress));
            });

        Dispatcher.Invoke(() => ApplyUpdateResult(result, isManual));
    }

    private void ApplyUpdateResult(UpdateCheckResult result, bool isManual)
    {
        _mainWindow?.ApplyUpdateCheckResult(result);

        if (result.Status == UpdateCheckStatus.UpdateAvailableNotifyOnly &&
            !string.IsNullOrWhiteSpace(result.AvailableVersion) &&
            !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            var alreadyNotified = string.Equals(
                _settings.LastNotifiedVersion,
                result.AvailableVersion,
                StringComparison.OrdinalIgnoreCase);

            if (isManual || !alreadyNotified)
            {
                _trayIcon?.ShowUpdateNotification(result.AvailableVersion, result.ReleaseUrl);

                if (!alreadyNotified)
                {
                    _settings = _settings with { LastNotifiedVersion = result.AvailableVersion };
                    _settingsStore.Save(_settings);
                }
            }
        }
        else if (result.Status == UpdateCheckStatus.UpdateDownloadedReadyToRestart &&
                 !string.IsNullOrWhiteSpace(result.AvailableVersion))
        {
            _trayIcon?.ShowUpdateReadyNotification(result.AvailableVersion);
        }
    }

    private void ApplyUpdateAndRestart()
    {
        if (!_updateService.HasPendingUpdate)
        {
            return;
        }

        _isExiting = true;
        _updateTimer?.Stop();
        _trayIcon?.Dispose();
        _updateService.ApplyPendingUpdateAndRestart();
    }

    private void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _updateTimer?.Stop();

        if (_updateService.HasPendingUpdate)
        {
            _updateService.ApplyPendingUpdateOnExit();
        }

        _trayIcon?.Dispose();
        _mainWindow?.AllowActualClose();
        _mainWindow?.Close();
        Shutdown();
    }
}
