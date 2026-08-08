; WSGM installer — elevated (admin) because the logon service is machine-wide:
; the service binary lives in Program Files (a SYSTEM service exe must never be
; user-writable) and stopping/registering it needs the SCM. The app itself stays
; per-user in %LOCALAPPDATA%\WSGM\bin. Consequence: run this setup from the
; handheld's (typically sole) admin account — {localappdata}/HKCU below belong
; to the ELEVATING user.
; Build via build.ps1 (publishes the app first, then compiles this).

#define AppName "WSGM - Windows Steam Game Mode"
; Version comes from the csproj <Version> via build.ps1 (/DAppVersion=...); the
; fallback below only applies when ISCC is invoked directly.
#ifndef AppVersion
  #define AppVersion "0.2.0"
#endif
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
; Per-user app layout (%LOCALAPPDATA%\WSGM\bin) written from an elevated setup —
; deliberate single-user-device design, see header. UsedUserAreasWarning quiets
; the compiler's warning about exactly that combination.
DefaultDirName={localappdata}\WSGM\bin
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
OutputDir=..\publish
OutputBaseFilename=WSGM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\WSGM.exe
CloseApplications=yes
; win-x64-only binary: refuse ARM64 (x64os, not x64compatible) — an emulated
; shell replacement is an untested configuration. Needs Inno Setup 6.3+.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
; WSGM reconstructs SteamOS Game Mode on Windows 11 (the per-user shell and
; game-mode scaling paths are only exercised there).
MinVersion=10.0.22000

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.SteamMissing=Steam was not found on this PC.%n%nWSGM is Steam-exclusive and boots straight into Steam Big Picture. Install Steam from steampowered.com, sign in once, and then run this setup again.
german.SteamMissing=Steam wurde auf diesem PC nicht gefunden.%n%nWSGM funktioniert ausschließlich mit Steam und startet direkt in Steam Big Picture. Installiere Steam von steampowered.com, melde dich einmal an und führe dieses Setup danach erneut aus.

[Files]
Source: "{#PublishDir}\WSGM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\WSGM.Deelevate.exe"; DestDir: "{app}"; Flags: ignoreversion
; SYSTEM service binary: Program Files only (admin-writable), never {app}. It
; launches the per-user WSGM.exe via the boot manifest — as that user, which is
; why the user-writable app path is not an escalation.
Source: "{#PublishDir}\WSGM.LogonService.exe"; DestDir: "{autopf}\WSGM"; Flags: ignoreversion
Source: "{#PublishDir}\steam-input-lease.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\SteamInputLease-*.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\SteamInputLease-*.md"; DestDir: "{app}"; Flags: ignoreversion
; Third-party license texts for managed packages (src\WSGM\Licenses\).
Source: "{#PublishDir}\LoadingIndicators.Avalonia-UNLICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\WSGM.exe"; Comment: "WSGM settings"

[Run]
; --setup: install per-user files, migrate OFF a legacy shell registration
; (restores the snapshotted previous shell), apply the Xbox-FSE guard, write the
; boot manifest. Runs elevated (whole setup is) — same single-user profile.
Filename: "{app}\WSGM.exe"; Parameters: "--setup"; Flags: runhidden
; Register + start the logon service (create-or-reconfigure also adopts an
; abandoned preview registration of the same name; PrepareToInstall already
; stopped it so [Files] could overwrite the binary).
Filename: "{autopf}\WSGM\WSGM.LogonService.exe"; Parameters: "--install"; Flags: runhidden waituntilterminated
; Update restart: if the shell was running it comes back as the shell; a plain
; settings instance comes back as settings (no args = DecideMode).
Filename: "{app}\WSGM.exe"; Parameters: "--shell"; Flags: nowait; Check: WasShellRunning
Filename: "{app}\WSGM.exe"; Flags: nowait; Check: WasSettingsRunning
Filename: "{app}\WSGM.exe"; Description: "Open WSGM settings"; Flags: nowait postinstall skipifsilent; Check: WasNothingRunning

[UninstallRun]
; Stop + delete the logon service FIRST — after files are gone the SCM would
; point at a missing binary and the next boot would log service-start failures.
Filename: "{autopf}\WSGM\WSGM.LogonService.exe"; Parameters: "--uninstall"; RunOnceId: "UninstallService"; Flags: runhidden skipifdoesntexist
; Legacy: restore a pre-service Winlogon shell registration BEFORE files are
; removed — otherwise the next logon would point at a deleted exe. Self-guarding
; no-op on service-boot installs. Quiet: no explorer start, no UI.
Filename: "{app}\WSGM.exe"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden
; Restore machine settings (UAC, lock-on-wake, ...) from the config snapshots
; while config.json still exists — [UninstallDelete] removes it afterwards.
Filename: "{app}\WSGM.exe"; Parameters: "--uninstall-restore"; RunOnceId: "UninstallRestore"; Flags: runhidden

[UninstallDelete]
; Config/logs live one level up; remove them with the app (per-user data only).
Type: filesandordirs; Name: "{localappdata}\WSGM"
; Service binary + service log directory (machine-wide pieces).
Type: filesandordirs; Name: "{autopf}\WSGM"
Type: filesandordirs; Name: "{commonappdata}\WSGM"

[InstallDelete]
; Remove the per-user staging helper left by service-based preview builds.
Type: files; Name: "{app}\WSGM.LogonService.exe"

[Code]
var
  WasShell: Boolean;
  WasRunning: Boolean;
  WasUpgrade: Boolean;

// Mirrors Core\Steam.cs detection: HKCU SteamExe (stored with forward slashes),
// then the machine-wide install dir. Detection only — the path is never stored.
function SteamInstalled(): Boolean;
var
  Exe, Dir: String;
begin
  Result := False;
  if RegQueryStringValue(HKCU, 'Software\Valve\Steam', 'SteamExe', Exe) then
  begin
    StringChangeEx(Exe, '/', '\', True);
    if (Exe <> '') and FileExists(Exe) then
    begin
      Result := True;
      Exit;
    end;
  end;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Valve\Steam', 'InstallPath', Dir) then
    if (Dir <> '') and FileExists(AddBackslash(Dir) + 'steam.exe') then
      Result := True;
end;

// Steam is WSGM's only prerequisite (the exe itself is NativeAOT self-contained).
// Without it an installed WSGM can only show its "install Steam" warning, so
// block setup up front and tell the user what to do instead.
function InitializeSetup(): Boolean;
begin
  // Capture this before [Files] creates/replaces WSGM.exe. The fixed per-user
  // location has no interactive directory page, so an existing payload means
  // this setup is updating an installed WSGM rather than performing a fresh install.
  WasUpgrade := FileExists(ExpandConstant('{localappdata}\WSGM\bin\WSGM.exe'));
  Result := SteamInstalled();
  if not Result then
    MsgBox(CustomMessage('SteamMissing'), mbCriticalError, MB_OK);
end;

// Killing elevated Steam is necessary to unload the injected payload, but it is
// disruptive enough that an interactive upgrade should offer Windows' standard
// Restart now / restart later choice on the Finished page. Never mark a silent
// upgrade for restart: /VERYSILENT would reboot automatically unless its caller
// happened to supply /NORESTART.
function NeedRestart(): Boolean;
begin
  Result := WasUpgrade and not WizardSilent();
end;

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
// may be ELEVATED — this unelevated setup/uninstaller cannot taskkill it.
// Instead WSGM listens on a named MANUAL-RESET event (one SetEvent releases
// every waiting instance, elevated or not) and exits itself gracefully (which
// also asks Steam to exit and releases the injected Steam Input payload).
// taskkill remains as fallback for
// unelevated leftovers. Returns True when the event existed, i.e. at least one
// WSGM instance was running.
function StopRunningInstances(): Boolean;
var
  R, I: Integer;
  H: THandle;
begin
  Result := False;
  H := OpenEventW($0002 { EVENT_MODIFY_STATE }, False, 'Local\WSGM.ExitForUpdate');
  if H <> 0 then
  begin
    SetEvent(H);
    CloseHandleK(H);
    Result := True;
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
end;

// Steam Input Lease injects its gate into steam.exe. The graceful WSGM update
// event above sends steam://exit from WSGM's (possibly elevated) token; give
// Steam a bounded chance to comply, then clean up leftovers. This is update-only:
// a user uninstalling WSGM should not have Steam force-closed as a side effect.
procedure StopSteamForUpdate();
var
  R: Integer;
begin
  Sleep(5000);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM steam.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  // Releases the new wrapper executable too. /T also stops its medium helper
  // and launched target; setup already shuts Steam down before this point.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.Deelevate.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

procedure StopDeelevationHelpers();
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.Deelevate.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
end;

// Stop the logon service BEFORE stopping WSGM. Ordering is load-bearing: with
// the service alive, a killed WSGM trips its watchdog, which starts explorer
// mid-update and flips the post-update restart into desktop mode. Also frees
// the service binary for [Files] (covers the abandoned preview too — same
// service name). Delete is not needed on updates; --install reconfigures.
procedure StopLogonService();
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop WSGMLogonService', '', SW_HIDE, ewWaitUntilTerminated, R);
  // sc stop is asynchronous; the service stops within a control cycle. A short
  // fixed wait suffices (the SCM refuses file locks only while START_PENDING).
  Sleep(1500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopLogonService();
  // Classify BEFORE killing anything: only the shell-mode instance holds this
  // mutex (session namespace). taskkill's exit code is deliberately NOT part of
  // WasRunning — /IM kills any WSGM.exe by image name (portable copies,
  // --overlay-test), so its success says nothing about the installed instance.
  // The event has the same blind spot (every run mode creates it), so a killed
  // unrelated instance can at worst restart as a settings window.
  WasShell := CheckForMutexes('WSGM.Shell');
  WasRunning := StopRunningInstances() or WasShell;
  StopSteamForUpdate();
  if WasRunning then
    Sleep(500);
  Result := '';
end;

// The uninstaller must stop a running WSGM too (in desktop mode — the only
// place Settings > Apps > Uninstall is reachable — WSGM stays resident), or
// WSGM.exe stays locked, file removal leaves 'could not be removed' leftovers
// and a zombie ex-shell process keeps running.
function InitializeUninstall(): Boolean;
begin
  StopLogonService();
  StopRunningInstances();
  StopDeelevationHelpers();
  Result := True;
end;
