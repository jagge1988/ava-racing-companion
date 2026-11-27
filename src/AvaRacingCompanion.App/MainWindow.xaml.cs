using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AvaRacingCompanion.Core.Config;
using AvaRacingCompanion.Core.Versioning;
using AvaRacingCompanion.Telemetry;
using AvaRacingCompanion.Telemetry.Api;
using AvaRacingCompanion.Updater;

namespace AvaRacingCompanion.App;

public partial class MainWindow : Window
{
    private readonly Action<AppSettings> _onSettingsChanged;
    private readonly Func<Task> _onManualUpdateCheckRequested;
    private readonly Action _onApplyUpdateNowRequested;
    private readonly Func<string, Task<ApiCallResult>> _onTestConnectionRequested;
    private readonly Func<string, string, Task<PairResult>> _onPairRequested;
    private readonly Action _onUnpairRequested;
    private readonly Func<bool> _onScanIbtRequested;

    private AppSettings _currentSettings;
    private string? _latestReleaseUrl;
    private bool _isInitializing = true;
    private bool _allowActualClose;

    public MainWindow(
        AppSettings initialSettings,
        bool isPaired,
        Action<AppSettings> onSettingsChanged,
        Func<Task> onManualUpdateCheckRequested,
        Action onApplyUpdateNowRequested,
        Func<string, Task<ApiCallResult>> onTestConnectionRequested,
        Func<string, string, Task<PairResult>> onPairRequested,
        Action onUnpairRequested,
        Func<bool> onScanIbtRequested)
    {
        InitializeComponent();

        _currentSettings = initialSettings;
        _onSettingsChanged = onSettingsChanged;
        _onManualUpdateCheckRequested = onManualUpdateCheckRequested;
        _onApplyUpdateNowRequested = onApplyUpdateNowRequested;
        _onTestConnectionRequested = onTestConnectionRequested;
        _onPairRequested = onPairRequested;
        _onUnpairRequested = onUnpairRequested;
        _onScanIbtRequested = onScanIbtRequested;

        VersionText.Text = $"v{AppInfo.GetCurrentVersion()}";
        AutoUpdateRadio.IsChecked = initialSettings.UpdateBehavior == UpdateBehavior.AutoUpdate;
        NotifyOnlyRadio.IsChecked = initialSettings.UpdateBehavior == UpdateBehavior.NotifyOnly;
        AutostartCheckBox.IsChecked = initialSettings.LaunchAtStartup;

        ServerUrlTextBox.Text = initialSettings.ServerBaseUrl;
        TelemetryEnabledCheckBox.IsChecked = initialSettings.TelemetryEnabled;
        UploadTracesCheckBox.IsChecked = initialSettings.UploadTracesEnabled;
        ImportIbtCheckBox.IsChecked = initialSettings.ImportIbtFilesEnabled;
        RefreshPairingUi(initialSettings, isPaired);

        _isInitializing = false;
    }

    public void AllowActualClose()
    {
        _allowActualClose = true;
    }

    public void RefreshPairingUi(AppSettings settings, bool isPaired)
    {
        _currentSettings = settings;
        if (isPaired)
        {
            UnpairedPanel.Visibility = Visibility.Collapsed;
            PairedPanel.Visibility = Visibility.Visible;
            var driverLabel = string.IsNullOrWhiteSpace(settings.PairedDriverName)
                ? "Discord-Konto"
                : settings.PairedDriverName;
            var custSuffix = settings.PairedCustId.HasValue ? $" (CustID {settings.PairedCustId.Value})" : string.Empty;
            PairedDriverText.Text = $"Gekoppelt als {driverLabel}{custSuffix}";
        }
        else
        {
            PairedPanel.Visibility = Visibility.Collapsed;
            UnpairedPanel.Visibility = Visibility.Visible;
        }
    }

    public void ShowAuthRevokedWarning()
    {
        ShowConnectionFeedback("Token widerrufen (HTTP 401 – z. B. per /unpair). Bitte mit neuem /pair-Code neu koppeln.");
    }

    public void ApplyTelemetryStatus(TelemetryRuntimeStatus status)
    {
        TelemetryStatusText.Text = status.StatusText;
        if (status.PendingUploads > 0)
        {
            PendingUploadsText.Text = $"{status.PendingUploads} offen";
            PendingUploadsBadge.Visibility = Visibility.Visible;
        }
        else
        {
            PendingUploadsBadge.Visibility = Visibility.Collapsed;
        }
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

        PersistCurrentUiSettings();
    }

    private void OnServerUrlLostFocus(object sender, RoutedEventArgs e)
    {
        if (_isInitializing)
        {
            return;
        }

        PersistCurrentUiSettings();
    }

    private void PersistCurrentUiSettings()
    {
        var behavior = NotifyOnlyRadio.IsChecked == true
            ? UpdateBehavior.NotifyOnly
            : UpdateBehavior.AutoUpdate;

        _currentSettings = _currentSettings with
        {
            UpdateBehavior = behavior,
            LaunchAtStartup = AutostartCheckBox.IsChecked == true,
            ServerBaseUrl = ServerUrlTextBox.Text ?? string.Empty,
            TelemetryEnabled = TelemetryEnabledCheckBox.IsChecked == true,
            UploadTracesEnabled = UploadTracesCheckBox.IsChecked == true,
            ImportIbtFilesEnabled = ImportIbtCheckBox.IsChecked == true
        };

        _onSettingsChanged(_currentSettings);
    }

    private void OnScanIbtClick(object sender, RoutedEventArgs e)
    {
        var started = _onScanIbtRequested();
        ShowConnectionFeedback(started
            ? "Suche nach neuen .ibt-Dateien im iRacing-Telemetrie-Ordner gestartet ..."
            : "Scan verschoben: Du sitzt aktuell im Auto (keine Hintergrund-Aktivität beim Fahren).");
    }

    private async void OnTestConnectionClick(object sender, RoutedEventArgs e)
    {
        PersistCurrentUiSettings();
        TestConnectionButton.IsEnabled = false;
        ShowConnectionFeedback("Prüfe Verbindung zum Bot-Server ...");

        try
        {
            var result = await _onTestConnectionRequested(ServerUrlTextBox.Text ?? string.Empty);
            ShowConnectionFeedback(result.Success
                ? "Verbindung erfolgreich! Bot-Ingest-API (v1) ist erreichbar."
                : $"Verbindung fehlgeschlagen: {result.ErrorMessage}");
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private async void OnPairClick(object sender, RoutedEventArgs e)
    {
        PersistCurrentUiSettings();
        PairButton.IsEnabled = false;
        ShowConnectionFeedback("Tausche /pair-Code gegen persönliches Geräte-Token ...");

        try
        {
            var result = await _onPairRequested(
                ServerUrlTextBox.Text ?? string.Empty,
                PairCodeTextBox.Text ?? string.Empty);

            if (result.Success && result.Response is not null)
            {
                PairCodeTextBox.Text = string.Empty;
                ShowConnectionFeedback($"Erfolgreich gekoppelt als {result.Response.DriverName}!");
            }
            else
            {
                ShowConnectionFeedback(result.ErrorMessage ?? "Kopplung fehlgeschlagen.");
            }
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void OnUnpairClick(object sender, RoutedEventArgs e)
    {
        _onUnpairRequested();
        ShowConnectionFeedback("Lokales Geräte-Token entfernt. Bei Bedarf zusätzlich /unpair im Discord nutzen.");
    }

    private void ShowConnectionFeedback(string message)
    {
        ConnectionFeedbackText.Text = message;
        ConnectionFeedbackText.Visibility = Visibility.Visible;
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
