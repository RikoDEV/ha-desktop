; Inno Setup script for HA Desktop.
;
; Local build:
;   ISCC.exe installer\windows\setup.iss
; (defaults to a debug version and expects a self-contained win-x64 publish
;  output at publish\win-x64 relative to the repo root)
;
; CI build (see .github/workflows/release.yml):
;   ISCC.exe /DMyAppVersion=1.2.3 /DSourceDir="C:\...\publish\win-x64" /O"C:\...\artifacts" installer\windows\setup.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\publish\win-x64"
#endif

#define MyAppName "HA Desktop"
#define MyAppPublisher "HA Desktop"
#define MyAppExeName "HaDesktop.Tray.exe"
#define MyAppURL "https://www.home-assistant.io/"

[Setup]
AppId={{9F1E1F2E-6C1A-4E7B-9E1F-2C6F6C1D2A55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={userpf}\HA Desktop
DefaultGroupName=HA Desktop
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\..\artifacts
OutputBaseFilename=HaDesktop-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\..\src\HaDesktop.Tray\Assets\tray-icon.ico
; Per-user install under the user's local Program Files — no admin rights needed.
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\HA Desktop"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall HA Desktop"; Filename: "{uninstallexe}"
Name: "{autodesktop}\HA Desktop"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,HA Desktop}"; Flags: nowait postinstall skipifsilent
