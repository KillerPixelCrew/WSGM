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

function OpenEventW(dwDesiredAccess: LongWord; bInheritHandle: BOOL; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandleK(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

// WSGM is almost certainly running during an update (it IS the shell), and it
// may be ELEVATED — this unelevated setup cannot taskkill it. Instead WSGM
// listens on a named event and exits itself gracefully (which also releases
// the Steam Input pin). taskkill remains as fallback for unelevated leftovers.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  R, I: Integer;
  H: THandle;
  Signaled: Boolean;
begin
  // Only the shell-mode instance holds this mutex (session namespace).
  WasShell := CheckForMutexes('WSGM.Shell');

  Signaled := False;
  H := OpenEventW($0002 { EVENT_MODIFY_STATE }, False, 'Local\WSGM.ExitForUpdate');
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandleK(H);
    Signaled := True;
    // Wait for the graceful exit (shell mutex disappears when the process dies).
    for I := 1 to 20 do
    begin
      if not CheckForMutexes('WSGM.Shell') then Break;
      Sleep(500);
    end;
    Sleep(500);
  end;

  // Fallback / leftovers (unelevated instances only — elevated ones already
  // exited via the event).
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.exe /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  WasRunning := WasShell or Signaled or (R = 0);
  if WasRunning then
    Sleep(500);
  Result := '';
end;
