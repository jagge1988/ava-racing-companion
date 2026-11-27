namespace AvaRacingCompanion.Core.Services;

/// <summary>
/// Abstraktion für die sichere lokale Speicherung des persönlichen Geräte-Tokens
/// (unter Windows per DPAPI benutzergebunden verschlüsselt).
/// </summary>
public interface ISecureTokenStore
{
    string? LoadToken();
    void SaveToken(string token);
    void DeleteToken();
}
