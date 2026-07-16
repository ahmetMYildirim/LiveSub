; Inno Setup script for LiveSub.
;
; Packages the self-contained Release publish output into LiveSub-Setup.exe.
; Build the publish output first:
;
;   dotnet publish PsGameTranslator.App/PsGameTranslator.App.csproj -c Release ^
;       -r win-x64 --self-contained true -o publish/LiveSub-win-x64
;
; then compile this script with ISCC.exe. The installer is per-user (no admin
; prompt) — the app writes its settings next to itself, so installing into
; Program Files would need elevation on every settings save.

#define AppName "LiveSub"
#define AppVersion "0.7.0"
#define AppPublisher "Ahmet Yildirim"
#define AppExeName "LiveSub.exe"
#define AppUrl "https://github.com/ahmetMYildirim/PsGameTranslator"
#define PublishDir "..\publish\LiveSub-win-x64"

[Setup]
AppId={{8F3C1B2E-6A47-4D19-9E85-1C7A0B4D2F63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Proprietary licence — shown and accepted before install.
LicenseFile=..\LICENSE
OutputDir=..\publish
OutputBaseFilename=LiveSub-Setup
SetupIconFile=..\PsGameTranslator.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install: no UAC prompt, and the app can write its own settings.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "turkish"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; PaddleOCR runs as a Python sidecar. Without a provisioned environment the app
; falls back to whatever system Python exists — which is how an install ends up
; with a silently broken OCR server and no subtitles. Creating <app>\.venv makes
; PythonResolver find a dedicated environment, exactly like a source checkout.
Name: "pythonenv"; Description: "Set up the PaddleOCR engine (needs Python 3.11 and ~1.5 GB of downloads)"; GroupDescription: "OCR engine:"

[Files]
; Everything from the self-contained publish output. The runtime settings file
; (config/user_settings.json) is never part of that output, so no API key can
; ever ride along into the installer.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; The bundled MIT/Apache/BSD/CC-BY components require their notices to travel
; with any redistributed build — these two files satisfy that obligation.
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
; Kept on disk so the user can re-run the OCR setup later without reinstalling.
Source: "setup_python_env.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Runs in a visible console: the PaddleOCR download takes many minutes and the
; user needs to see progress (and any Python-missing message) rather than stare
; at a frozen wizard.
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\setup_python_env.ps1"" -AppDir ""{app}"""; \
  StatusMsg: "Setting up the PaddleOCR engine (this can take several minutes)..."; \
  Flags: waituntilterminated; Tasks: pythonenv
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The provisioned Python environment is ours — remove it on uninstall.
Type: filesandordirs; Name: "{app}\.venv"
; Logs, debug frames and the user's own settings are created at runtime next to
; the app; remove them so an uninstall leaves nothing behind.
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\debug"
Type: filesandordirs; Name: "{app}\config\user_settings.json"
