using System.Diagnostics;
using System.Drawing;
using AvaRacingCompanion.Core.Versioning;
using WinForms = System.Windows.Forms;

namespace AvaRacingCompanion.App.Tray;

/// <summary>
/// Kapselt das Windows-System-Tray-Icon samt Kontextmenü und dezenten Update-Benachrichtigungen.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private string? _pendingBalloonUrl;
    private bool _disposed;

    public event EventHandler? OpenRequested;
    public event EventHandler? CheckForUpdatesRequested;
    public event EventHandler? ExitRequested;

    public TrayIconManager()
    {
        _icon = LoadAppIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Öffnen", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Auf Updates prüfen", null, (_, _) => CheckForUpdatesRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = AppInfo.DisplayName,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        _notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
    }

    public void ShowUpdateNotification(string version, string releaseUrl)
    {
        _pendingBalloonUrl = releaseUrl;
        _notifyIcon.ShowBalloonTip(
            timeout: 8000,
            tipTitle: $"{AppInfo.DisplayName} – Update verfügbar",
            tipText: $"Version v{version} ist auf GitHub verfügbar. Klicken, um Release & Changelog zu öffnen.",
            tipIcon: WinForms.ToolTipIcon.Info);
    }

    public void ShowUpdateReadyNotification(string version)
    {
        _pendingBalloonUrl = null;
        _notifyIcon.ShowBalloonTip(
            timeout: 8000,
            tipTitle: $"{AppInfo.DisplayName} – Update bereit",
            tipText: $"Version v{version} wurde heruntergeladen und wird beim nächsten Neustart installiert.",
            tipIcon: WinForms.ToolTipIcon.Info);
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_pendingBalloonUrl))
        {
            OpenUrl(_pendingBalloonUrl);
            _pendingBalloonUrl = null;
        }
        else
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch
        {
            // Fallback auf System-Standard-Icon
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignorieren, falls kein Standardbrowser registriert ist
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
