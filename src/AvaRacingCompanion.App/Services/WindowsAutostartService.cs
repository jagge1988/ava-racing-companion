using AvaRacingCompanion.Core.Services;
using AvaRacingCompanion.Core.Versioning;
using Microsoft.Win32;

namespace AvaRacingCompanion.App.Services;

/// <summary>
/// Verwaltet den Windows-Autostart ausschließlich im Benutzerkontext (HKCU, keine Admin-Rechte nötig).
/// </summary>
public sealed class WindowsAutostartService : IAutostartService
{
    private const string RunRegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string MinimizedFlag = "--minimized";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: false);
        var value = key?.GetValue(AppInfo.AppId) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunRegistryKeyPath, writable: true);

        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return;
            }

            key.SetValue(AppInfo.AppId, $"\"{exePath}\" {MinimizedFlag}", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(AppInfo.AppId, throwOnMissingValue: false);
        }
    }
}
