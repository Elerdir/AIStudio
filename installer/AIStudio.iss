; ══════════════════════════════════════════════════════════════════════════════
;  AI Studio – Inno Setup 6 install script
;  Generuje self-contained installer pro Windows x64
; ══════════════════════════════════════════════════════════════════════════════

#define AppName      "AI Studio"
#define AppVersion   "0.1.0"
#define AppPublisher "OKsystem"
#define AppExeName   "AIStudio.App.exe"
#define AppId        "{{B4E2A8C7-1F3D-4E9B-A25C-7D8F0E6B3A14}"
#define PublishDir   "..\publish\win-x64"
#define IconFile     "..\AIStudio.App\Assets\avalonia-logo.ico"

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/oksystem/aistudio
AppSupportURL=https://github.com/oksystem/aistudio/issues
AppUpdatesURL=https://github.com/oksystem/aistudio/releases

; Instalace per-user (bez UAC) nebo system-wide při spuštění jako admin
; {autopf} = C:\Program Files při admin, %LocalAppData%\Programs jinak
DefaultDirName={autopf}\AIStudio
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; Jen 64-bit Windows
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Windows 10 1903+ (19H1) – minimální požadavek pro .NET 10
MinVersion=10.0.18362

; Výstup
OutputDir=..\dist
OutputBaseFilename=AIStudio-Setup-{#AppVersion}

; Komprese
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes

; Vzhled
WizardStyle=modern
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}

; Přidat do Přidat/odebrat programy
CreateUninstallRegKey=yes

; Zavři aplikaci před aktualizací
CloseApplications=yes
CloseApplicationsFilter=*.exe

; Logy
SetupLogging=yes

[Languages]
Name: "czech";   MessagesFile: "compiler:Languages\Czech.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
    Description: "Vytvořit zástupce na ploše"; \
    GroupDescription: "Zástupce:";

[Files]
; ── Celý publish output ──────────────────────────────────────────────────────
Source: "{#PublishDir}\*"; \
    DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start Menu
Name: "{group}\{#AppName}";                   Filename: "{app}\{#AppExeName}"
Name: "{group}\Odinstalovat {#AppName}";      Filename: "{uninstallexe}"

; Plocha (volitelné)
Name: "{autodesktop}\{#AppName}"; \
    Filename: "{app}\{#AppExeName}"; \
    Tasks: desktopicon

[Run]
; Nabídni spuštění aplikace po instalaci
Filename: "{app}\{#AppExeName}"; \
    Description: "Spustit {#AppName}"; \
    Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; POZOR: Data uživatele (konverzace, modely) se NEMAŽOU.
; Jsou uložena v %AppData%\AIStudio — smaž ručně pokud chceš.
; Gdybys chtěl smazat i data, odkomentuj řádky níže:
; Type: filesandordirs; Name: "{localappdata}\AIStudio"
; Type: filesandordirs; Name: "{commonappdata}\AIStudio"

[Registry]
; Aplikace se zaregistruje v Add/Remove Programs automaticky přes AppId výše.
; Volitelně: přidej do registru cestu ke spustitelnému souboru
Root: HKCU; Subkey: "Software\OKsystem\AIStudio"; \
    ValueType: string; ValueName: "InstallPath"; ValueData: "{app}"; \
    Flags: uninsdeletekey

[Code]
// ── Kontrola: není nainstalovaná starší verze? ─────────────────────────────
function InitializeSetup(): Boolean;
var
  oldVersion: String;
begin
  Result := True;

  if RegQueryStringValue(HKCU, 'Software\OKsystem\AIStudio',
                         'InstallPath', oldVersion) then
  begin
    // Nic neblokuj – prostě přeinstaluj
  end;
end;
