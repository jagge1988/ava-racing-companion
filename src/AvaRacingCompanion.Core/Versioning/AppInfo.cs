using System.Reflection;

namespace AvaRacingCompanion.Core.Versioning;

/// <summary>
/// Zentrale, statische Metadaten der Anwendung (keine Secrets oder Tokens).
/// </summary>
public static class AppInfo
{
    /// <summary>
    /// Eindeutige Paket-ID für Velopack, AppData-Ordner, Mutex und Autostart-Eintrag.
    /// Darf zwischen Releases nicht geändert werden.
    /// </summary>
    public const string AppId = "AvaRacingCompanion";

    public const string DisplayName = "AVA Racing Companion";

    /// <summary>
    /// GitHub-Repository im Format "owner/repo".
    /// Bei Bedarf hier und in Directory.Build.props anpassen.
    /// </summary>
    public const string GitHubRepositorySlug = "jagge1988/ava-racing-companion";

    public static string GitHubRepositoryUrl => $"https://github.com/{GitHubRepositorySlug}";

    public static string GitHubReleasesUrl => $"{GitHubRepositoryUrl}/releases";

    public static string GetReleaseTagUrl(string version)
    {
        var normalized = version.Trim().TrimStart('v', 'V');
        return $"{GitHubRepositoryUrl}/releases/tag/v{normalized}";
    }

    public static string GetCurrentVersion(Assembly? assembly = null)
    {
        var target = assembly ?? Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

        var informational = target
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+');
            return plusIndex > 0 ? informational[..plusIndex] : informational;
        }

        var version = target.GetName().Version;
        return version is not null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
    }
}
