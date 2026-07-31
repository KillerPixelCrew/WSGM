; WSGM installer — per-user, no admin rights, no UAC prompt.
; Build via build.ps1 (publishes the app first, then compiles this).

#define AppName "WSGM - Windows Steam Game Mode"
#define AppVersion "0.1.0"
#define AppPublisher "NightHammer1000"
#define AppURL "https://github.com/NightHammer1000/WSGM"
#define PublishDir "..\publish"

[Setup]
; New product identity (renamed from OpenFSE) — a fresh AppId so the old OpenFSE
; install is not silently upgraded; uninstall OpenFSE separately.
AppId={{E4C7A9D2-58F1-4B36-A2C4-7D9E31B0F5C8}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppURL}
; Per-user install: matches the app's own layout (%LOCALAPPDATA%\WSGM\bin)
DefaultDirName={localappdata}\WSGM\bin
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=WSGM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\WSGM.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "{#PublishDir}\WSGM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\WSGM.exe"; Comment: "WSGM settings"

[Run]
; Update restart: if the shell was running it comes back as the shell; a plain
; settings instance comes back as settings (no args = DecideMode).
Filename: "{app}\WSGM.exe"; Parameters: "--shell"; Flags: nowait; Check: WasShellRunning
Filename: "{app}\WSGM.exe"; Flags: nowait; Check: WasSettingsRunning
Filename: "{app}\WSGM.exe"; Description: "Open WSGM settings"; Flags: nowait postinstall skipifsilent; Check: WasNothingRunning

[UninstallRun]
; Restore the previous Windows shell BEFORE files are removed — otherwise the next
; logon would point at a deleted exe. Quiet: no explorer start, no UI.
Filename: "{app}\WSGM.exe"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden

[UninstallDelete]
; Config/logs live one level up; remove them with the app (per-user data only).
Type: filesandordirs; Name: "{localappdata}\WSGM"

[Code]
var
  WasShell: Boolean;
  WasRunning: Boolean;

function WasShellRunning(): Boolean;
begin
  Result := WasShell;
end;

function WasSettingsRunning(): Boolean;
begin
  Result := WasRunning and not WasShell;
end;

function WasNothingRunning(): Boolean;
begin
  Result := not WasRunning;
end;

// WSGM is almost certainly running during an update (it IS the shell). Remember
// what was running, kill it so the files can be replaced, and the [Run] section
// restarts it in the mode it had. Kill twice with a pause in between in case
// Winlogon's AutoRestartShell resurrects the shell process from the old exe.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R: Integer;
begin
  // Only the shell-mode instance holds this mutex (session namespace).
  WasShell := CheckForMutexes('WSGM.Shell');
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  WasRunning := WasShell or (R = 0);
  if WasRunning then
  begin
    Sleep(1500);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
    Sleep(500);
  end;
  Result := '';
end;
