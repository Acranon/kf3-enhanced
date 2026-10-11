; Inno Setup script for King's Field III Enhanced.
;
; Run packaging\windows\build-windows.ps1, which publishes the tree this installs and
; then calls iscc with the version set.
;
; The installer does not ask for the disc. The game is built on first launch, from
; the image the player picks in the app, into %LOCALAPPDATA%\kf3-enhanced, so the
; install folder stays read-only, an uninstall leaves saves alone, and a reinstall
; does not force a rebuild.

#define AppName    "King's Field III Enhanced"
#define AppVersion GetEnv("KF3_VERSION")
#if AppVersion == ""
  #error KF3_VERSION is not set. Run packaging\windows\build-windows.ps1 rather than iscc directly.
#endif

[Setup]
AppId={{6B0E3C55-1F4B-4C3B-9E8A-3D2A7F1C9B40}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Acranon
AppPublisherURL=https://github.com/Acranon/kf3-enhanced
DefaultDirName={autopf}\KF3Enhanced
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\KF3Enhanced.exe
OutputDir=..\..\dist
OutputBaseFilename=KF3Enhanced-{#AppVersion}-win-x64-setup
SetupIconFile=..\icon\kf3.ico
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
DisableProgramGroupPage=yes
; Per-user by default (no administrator prompt); the player can still choose all users.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
InfoBeforeFile=first-run.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\..\dist\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Sources from an older version that no longer exist must not be compiled into the
; next build.
Type: filesandordirs; Name: "{app}\content"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\KF3Enhanced.exe"
Name: "{group}\Saves and settings"; Filename: "{localappdata}\kf3-enhanced"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\KF3Enhanced.exe"; Tasks: desktopicon

[Dirs]
Name: "{localappdata}\kf3-enhanced"

[Run]
Filename: "{app}\KF3Enhanced.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Saves, settings and the built game in %LOCALAPPDATA%\kf3-enhanced are NOT removed
; on uninstall: a lost save cannot be rebuilt.
