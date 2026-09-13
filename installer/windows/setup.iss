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
; Shows the MIT license on its own wizard page; the user must accept before continuing.
LicenseFile=..\..\LICENSE
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
; A running copy holds every file we are about to overwrite, and as a tray app with no top-level
; window Restart Manager doesn't reliably shut it down — so the [Code] section below terminates it
; outright. Deliberately no AppMutex here: that check runs before PrepareToInstall and would
; prompt the user to close the app manually instead of letting the automatic kill happen.
; RestartApplications=no: we relaunch via [Run]/autostart ourselves.
CloseApplications=yes
CloseApplicationsFilter=*.exe,*.dll
RestartApplications=no

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

[Code]
{ Terminates any running HA Desktop. taskkill exits with 128 ("no tasks matching") once none are
  left, which is how we know the files are free; /F is needed because the app is a tray-only
  process with no window to send a close request to. Returns False if something is still alive
  after a few tries — e.g. a copy running under a different account, which a per-user,
  unelevated setup can't touch. }
function TerminateRunningApp(): Boolean;
var
  ResultCode, Attempt: Integer;
begin
  Result := False;
  for Attempt := 1 to 10 do
  begin
    if not Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "{#MyAppExeName}"', '',
                SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    begin
      { taskkill itself couldn't be launched — nothing more we can do here. }
      Result := True;
      Exit;
    end;

    if ResultCode = 128 then
    begin
      Result := True;
      Exit;
    end;

    { A killed process still needs a moment to release its file handles and the mutex. }
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not TerminateRunningApp() then
    Result := 'HA Desktop is still running and could not be closed automatically.' + #13#10 +
              'Please exit it from the system tray and run setup again.';
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  if not TerminateRunningApp() then
  begin
    MsgBox('HA Desktop is still running and could not be closed automatically.' + #13#10 +
           'Please exit it from the system tray and run the uninstaller again.',
           mbError, MB_OK);
    Result := False;
  end;
end;
