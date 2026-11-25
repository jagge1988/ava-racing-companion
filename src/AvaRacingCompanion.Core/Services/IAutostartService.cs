namespace AvaRacingCompanion.Core.Services;

/// <summary>
/// Abstraktion für das Aktivieren/Deaktivieren des Windows-Autostarts.
/// </summary>
public interface IAutostartService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}
