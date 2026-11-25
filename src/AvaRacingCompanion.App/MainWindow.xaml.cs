using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Core.Versioning;
using AvaRacingCompanion.Updater;

namespace AvaRacingCompanion.App;

public partial class MainWindow : Window
{
    private readonly Action<AppSettings> _onSettingsChanged;
    private readonly Func<Task> _onManualUpdateCheckRequested;
    private readonly Action _onApplyUpdateNowRequested;

    private AppSettings _currentSettings;
    private string? _latestReleaseUrl;
    private bool _isInitializing = true;
    private bool _allowActualClose;

    public MainWindow(
        AppSettings initialSettings,
        Action<AppSettings> onSettingsChanged,
        Func<Task> onManualUpdateCheckRequested,
        Action onApplyUpdateNowRequested)
    {
        InitializeComponent();

        _currentSettings = initialSettings;
        _onSettingsChanged = onSettingsChanged;
        _onManualUpdateCheckRequested = onManualUpdateCheckRequested;
        _onApplyUpdateNowRequested = onApplyUpdateNowRequested;

        VersionText.Text = $"v{AppInfo.GetCurrentVersion()}";
        AutoUpdateRadio.IsChecked = initialSettings.UpdateBehavior == UpdateBehavior.AutoUpdate;
        NotifyOnlyRadio.IsChecked = initialSettings.UpdateBehavior == UpdateBehavior.NotifyOnly;
        AutostartCheckBox.IsChecked = initialSettings.LaunchAtStartup;

        _isInitializing = false;
    }

    public void AllowActualClose()
    {
        _allowActualClose = true;
    }

    public void SetUpdateCheckingState()
    {
        CheckUpdatesButton.IsEnabled = false;
        UpdateStatusText.Text = "Prüfe GitHub Releases auf Updates ...";
    }

    public void SetDownloadProgress(int progressPercent)
    {
        UpdateStatusText.Text = $"Lade Update herunter ... ({progressPercent} %)";
    }

    public void ApplyUpdateCheckResult(UpdateCheckResult result)
    {
        CheckUpdatesButton.IsEnabled = true;
        _latestReleaseUrl = result.ReleaseUrl;

        UpdateStatusText.Text = result.Message ?? "Status aktualisiert.";

        ApplyUpdateButton.Visibility = result.Status == UpdateCheckStatus.UpdateDownloadedReadyToRestart
            ? Visibility.Visible
            : Visibility.Collapsed;

        OpenReleaseButton.Visibility = result.Status == UpdateCheckStatus.UpdateAvailableNotifyOnly
            && !string.IsNullOrWhiteSpace(result.ReleaseUrl)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowActualClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnSettingsControlChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        var behavior = NotifyOnlyRadio.IsChecked == true
            ? UpdateBehavior.NotifyOnly
            : UpdateBehavior.AutoUpdate;

        var launchAtStartup = AutostartCheckBox.IsChecked == true;

        _currentSettings = _currentSettings with
        {
            UpdateBehavior = behavior,
            LaunchAtStartup = launchAtStartup
        };

        _onSettingsChanged(_currentSettings);
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        await _onManualUpdateCheckRequested();
    }

    private void OnApplyUpdateClick(object sender, RoutedEventArgs e)
    {
        _onApplyUpdateNowRequested();
    }

    private void OnOpenReleaseClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_latestReleaseUrl))
        {
            OpenUrl(_latestReleaseUrl);
        }
    }

    private void OnOpenAllReleasesClick(object sender, RoutedEventArgs e)
    {
        OpenUrl(AppInfo.GitHubReleasesUrl);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignorieren, falls kein Standardbrowser verfügbar ist
        }
    }
}
