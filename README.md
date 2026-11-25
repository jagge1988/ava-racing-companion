# AVA Racing Companion

Eigenständige Windows-Companion-App für unser iRacing-Team (**AVA Racing**).  
Dieses Repository ist bewusst **öffentlich einsehbar und schlank aufgebaut**, damit jedes Teammitglied den vollständigen Quellcode vor der Installation selbst prüfen (auditieren) kann.

> **Aktueller Stand (Phase 1 – Fundament):**  
> Diese erste Ausbaustufe enthält ausschließlich die saubere Basis: System-Tray-App, schlichtes Einstellungsfenster, abschaltbarer GitHub-Auto-Updater (Velopack) sowie reproduzierbare GitHub-Actions-Builds inkl. kryptografischem Provenance-Nachweis. **Es ist noch keine iRacing-SDK- oder `.ibt`-Logik enthalten** (folgt in Phase 2 im Modul `AvaRacingCompanion.Telemetry`).

---

## Sicherheits- & Transparenzprinzipien

- **Keine Secrets oder Bot-Tokens im Repository:** Alle Konfigurationen liegen lokal unter `%APPDATA%\AvaRacingCompanion\settings.json`.
- **Minimaler Abhängigkeitsbaum:** Produktiv wird neben dem offiziellen .NET-8-Desktop-SDK **nur ein einziges externes NuGet-Paket (`Velopack`)** verwendet.
- **Keine Administratorrechte erforderlich:** Installation (Velopack User-Scope) und Windows-Autostart (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) laufen ausschließlich im Benutzerkontext.
- **Reproduzierbare Releases mit Build-Nachweis:** Jedes Release wird direkt aus einem Git-Tag (`v*.*.*`) auf `windows-latest` gebaut, mit `SHA256SUMS.txt` versehen und über `actions/attest-build-provenance` kryptografisch attestiert.

---

## Projektstruktur

```text
ava-racing-companion/
├── .github/workflows/
│   ├── ci.yml                         # Automatischer Build & Unit-Tests bei Push/PR
│   └── release.yml                    # Tag v*.*.* → Installer, Velopack-Paket, SHA256SUMS, Provenance, optionales Signing
├── src/
│   ├── AvaRacingCompanion.Core/       # Einstellungen (JSON), Versionierung, Interfaces (0 externe Pakete)
│   ├── AvaRacingCompanion.Updater/    # Velopack-Integration gegen GitHub Releases (Auto-Update vs. Nur benachrichtigen)
│   ├── AvaRacingCompanion.Telemetry/  # Platzhalter für Phase 2 (iRacing SDK & .ibt-Telemetrie)
│   └── AvaRacingCompanion.App/        # WPF-Hauptfenster, System-Tray-Icon, Windows-Autostart
└── tests/
    └── AvaRacingCompanion.Core.Tests/ # xUnit-Tests für Core-Logik
```

> **Hinweis zum GitHub-Repository-Slug:**  
> Der zentrale Repository-Verweis (`jagge1988/ava-racing-companion`) steht an genau zwei Stellen:  
> 1. `src/AvaRacingCompanion.Core/Versioning/AppInfo.cs` (`GitHubRepositorySlug`)  
> 2. `Directory.Build.props` (`RepositoryUrl`)  
> Falls das GitHub-Repository unter einem anderen Namen angelegt wird, bitte dort anpassen.

---

## Update-Verhalten (abschaltbar)

Die App prüft beim Start (leicht verzögert) sowie periodisch im Hintergrund gegen die GitHub Releases dieses Repositories. In der App lässt sich dauerhaft wählen:

1. **Auto-Update aktiv (Standard):**  
   Lädt neue Releases automatisch im Hintergrund herunter und installiert sie beim nächsten Beenden/Neustart oder sofort per Klick auf **„Jetzt aktualisieren“**.
2. **Auto-Update deaktiviert (Nur benachrichtigen):**  
   Lädt **keine Binärdateien ungefragt herunter**. Es werden lediglich Release-Metadaten geprüft und bei einer neuen Version eine dezente Windows-Tray-Benachrichtigung mit Versionsnummer und direktem Link zum GitHub-Release/Changelog angezeigt.

Zusätzlich steht jederzeit der manuelle Button **„Jetzt auf Updates prüfen“** (im Hauptfenster und im Tray-Kontextmenü) bereit.

---

## Lokal prüfen & selbst bauen (Windows)

Voraussetzung: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) auf Windows 10/11.

```powershell
# 1. Repository klonen
git clone https://github.com/jagge1988/ava-racing-companion.git
cd ava-racing-companion

# 2. Lösung bauen und Unit-Tests ausführen
dotnet build AvaRacingCompanion.sln -c Release
dotnet test AvaRacingCompanion.sln -c Release

# 3. App direkt starten (ohne Installer; Updater zeigt dann Hinweis auf Entwicklungs-Build)
dotnet run --project src/AvaRacingCompanion.App/AvaRacingCompanion.App.csproj
```

---

## Release-Verifizierung für Teammitglieder

Jedes offizielle Release unter **GitHub Releases** enthält:
- `AvaRacingCompanion-win-Setup.exe` (Installer)
- `AvaRacingCompanion-<version>-full.nupkg` & `releases.win.json` (Velopack-Update-Dateien)
- `SHA256SUMS.txt` (Prüfsummen aller Dateien)
- Offizielle **GitHub Build Provenance Attestation**

### 1. SHA-256-Prüfsumme lokal vergleichen (PowerShell)

```powershell
Get-FileHash .\AvaRacingCompanion-win-Setup.exe -Algorithm SHA256
Get-Content .\SHA256SUMS.txt
```

### 2. Kryptografischen Build-Nachweis prüfen (`gh attestation verify`)

Mit der offiziellen [GitHub CLI (`gh`)](https://cli.github.com/) kann jedes Teammitglied prüfen, dass die heruntergeladene `.exe` **nachweislich und unverändert** von GitHub Actions aus genau diesem öffentlichen Repository gebaut wurde:

```powershell
gh attestation verify .\AvaRacingCompanion-win-Setup.exe --repo jagge1988/ava-racing-companion
```

Bei erfolgreicher Prüfung gibt `gh` den exakten Commit-SHA, den Workflow (`.github/workflows/release.yml`) und die erfolgreiche Signaturprüfung aus.

---

## Neues Release veröffentlichen

Sobald der Code auf `main` bereit ist, genügt ein SemVer-Tag:

```bash
git tag v0.1.0
git push origin v0.1.0
```

GitHub Actions baut daraufhin automatisch den Installer + das Velopack-Paket, erzeugt `SHA256SUMS.txt`, erstellt die Build-Provenance-Attestation und veröffentlicht das GitHub Release.

---

## Einrichtung von Windows Code Signing (gegen SmartScreen-Warnungen)

Solange keine Signing-Secrets hinterlegt sind, fällt `.github/workflows/release.yml` sauber auf einen **unsignierten Build** zurück (Windows SmartScreen zeigt dann beim ersten Start ggf. „Weitere Informationen → Trotzdem ausführen“).

Der Workflow ist bereits für zwei etablierte Varianten vorbereitet:

### Option A: SignPath.io (kostenlos für Open Source)
1. Kostenloses Open-Source-Projekt bei [SignPath.io](https://about.signpath.io/product/open-source) beantragen und mit diesem GitHub-Repo verbinden.
2. In den GitHub-Repository-Einstellungen unter **Settings → Secrets and variables → Actions** anlegen:
   - **Variables:**
     - `SIGNING_PROVIDER` = `signpath`
     - `SIGNPATH_ORGANIZATION_ID` = `<SignPath Org ID>`
     - `SIGNPATH_PROJECT_SLUG` = `<Projekt-Slug>`
     - `SIGNPATH_SIGNING_POLICY_SLUG` = `release-signing`
   - **Secrets:**
     - `SIGNPATH_API_TOKEN` = `<API-Token>`

### Option B: Azure Trusted Signing
1. Azure Trusted Signing Account + Certificate Profile einrichten und GitHub OIDC Federated Credential hinterlegen.
2. In den GitHub-Repository-Einstellungen anlegen:
   - **Variables:**
     - `SIGNING_PROVIDER` = `azure`
     - `AZURE_SIGNING_ENDPOINT` = z. B. `https://weu.codesigning.azure.net/`
     - `AZURE_SIGNING_ACCOUNT` = `<Account-Name>`
     - `AZURE_SIGNING_CERT_PROFILE` = `<Certificate-Profile-Name>`
   - **Secrets:**
     - `AZURE_CLIENT_ID`
     - `AZURE_TENANT_ID`
     - `AZURE_SUBSCRIPTION_ID`
