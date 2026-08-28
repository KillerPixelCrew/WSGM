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
#define PublishRoot "..\publish"
#define AppPublishDir "..\publish\App"
#define DeviceHostPublishDir "..\publish\DeviceHost"
#define DevicePackagesPublishDir "..\publish\Packages"
#define DeviceToolsPublishDir "..\publish\Tools"

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
OutputDir={#PublishRoot}
OutputBaseFilename=WSGM-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\WSGM.exe
SetupIconFile=..\src\WSGM\Assets\wsgm.ico
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

[Types]
; Core-only is first and therefore the unattended/default choice. Installing the
; Device Integration bytes is still inert: config defaults off and no DeviceHost
; starts until the user explicitly enables the feature in WSGM Settings.
Name: "core"; Description: "Core WSGM"
Name: "full"; Description: "Core WSGM + Device Integration"
Name: "custom"; Description: "Custom"; Flags: iscustom

[Components]
Name: "core"; Description: "Core WSGM"; Types: core full custom; Flags: fixed
Name: "device"; Description: "Device Integration runtime and reviewed device packages (remains disabled until enabled in WSGM Settings)"; Types: full
Name: "devicelab"; Description: "Device Lab and offline device-development tools"; Types: custom

[CustomMessages]
english.SteamMissing=Steam was not found on this PC.%n%nWSGM is Steam-exclusive and boots straight into Steam Big Picture. Install Steam from steampowered.com, sign in once, and then run this setup again.
german.SteamMissing=Steam wurde auf diesem PC nicht gefunden.%n%nWSGM funktioniert ausschließlich mit Steam und startet direkt in Steam Big Picture. Installiere Steam von steampowered.com, melde dich einmal an und führe dieses Setup danach erneut aus.

[Files]
; Only the NativeAOT app component is visible to this installer section. DeviceHost,
; Device Lab, and plugin packages are staged in sibling component directories, so
; the legacy DLL glob below cannot accidentally flatten JIT/plugin dependencies into {app}.
Source: "{#AppPublishDir}\WSGM.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\WSGM.Launch.exe"; DestDir: "{app}"; Flags: ignoreversion
; SYSTEM service binary: Program Files only (admin-writable), never {app}. It
; launches the per-user WSGM.exe via the boot manifest — as that user, which is
; why the user-writable app path is not an escalation.
Source: "{#AppPublishDir}\WSGM.LogonService.exe"; DestDir: "{autopf}\WSGM"; Flags: ignoreversion
; Read-only radio diagnostic. Reports what the docs cannot settle for a given
; machine: whether radio control works with no shell running, and whether the
; Wi-Fi scan is blocked by the location-consent gate.
Source: "{#AppPublishDir}\WSGM.RadioProbe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\SteamInputLease-*.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppPublishDir}\SteamInputLease-*.md"; DestDir: "{app}"; Flags: ignoreversion
; Third-party license texts for managed packages (src\WSGM\Licenses\).
Source: "{#AppPublishDir}\LoadingIndicators.Avalonia-UNLICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; Reviewed DeviceHost and plugin packages are administrator-protected. Runtime
; discovery never trusts a user-writable copy for the WSGM-reviewed tier.
Source: "{#DeviceHostPublishDir}\*"; DestDir: "{autopf}\WSGM\DeviceHost"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device
Source: "{#DevicePackagesPublishDir}\*"; DestDir: "{autopf}\WSGM\DevicePlugins\reviewed"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device
; Device Lab never owns the production cycle and remains an explicit custom
; component. Its probe-host mutation commands still require interactive consent.
Source: "{#DeviceToolsPublishDir}\*"; DestDir: "{app}\Tools"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: devicelab

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
; Remove the Steam Input shim from STEAM's directory before anything else — it is
; the only file WSGM puts outside its own install, it needs {app}\WSGM.exe to
; still exist, and only WSGM can tell its own copy from a same-named file another
; tool (ValvePlug, Special K) owns. [UninstallDelete] deliberately cannot do this.
Filename: "{app}\WSGM.exe"; Parameters: "--remove-steam-input-shim"; RunOnceId: "RemoveSteamInputShim"; Flags: runhidden skipifdoesntexist
; Stop + delete the logon service FIRST — after files are gone the SCM would
; point at a missing binary and the next boot would log service-start failures.
Filename: "{autopf}\WSGM\WSGM.LogonService.exe"; Parameters: "--uninstall"; RunOnceId: "UninstallService"; Flags: runhidden skipifdoesntexist
; Legacy: restore a pre-service Winlogon shell registration BEFORE files are
; removed — otherwise the next logon would point at a deleted exe. Self-guarding
; no-op on service-boot installs. Quiet: no explorer start, no UI.
Filename: "{app}\WSGM.exe"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden skipifdoesntexist
; Restore machine settings (UAC, lock-on-wake, ...) from the config snapshots
; while config.json still exists — [UninstallDelete] removes it afterwards.
Filename: "{app}\WSGM.exe"; Parameters: "--uninstall-restore"; RunOnceId: "UninstallRestore"; Flags: runhidden skipifdoesntexist

[UninstallDelete]
; Config/logs live one level up; remove them with the app (per-user data only).
Type: filesandordirs; Name: "{localappdata}\WSGM"
; Service binary + service log directory (machine-wide pieces).
Type: filesandordirs; Name: "{autopf}\WSGM"
Type: filesandordirs; Name: "{commonappdata}\WSGM"

[InstallDelete]
; Remove the per-user staging helper left by service-based preview builds.
Type: files; Name: "{app}\WSGM.LogonService.exe"
; The two wrappers WSGM.Launch.exe replaces. Deleting them is deliberate: a stale
; helper would keep an old pasted launch option working, so the two would drift
; apart silently. Gone, the option fails visibly and the release note explains it.
Type: files; Name: "{app}\WSGM.Deelevate.exe"
Type: files; Name: "{app}\steam-input-lease.exe"

[Code]
type
  TSystemTime = record
    Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds: Word;
  end;

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
procedure GetSystemTime(var SystemTime: TSystemTime);
  external 'GetSystemTime@kernel32.dll stdcall';

// The build writes a deterministic epoch into each installer-owned package
// grant. Installation replaces only that excluded metadata field; every payload
// hash remains the exact value generated after signing.
procedure StampReviewedPackageRecords(const Directory: String);
var
  FindRec: TFindRec;
  Path, Content, InstalledAt: String;
  SystemTime: TSystemTime;
begin
  if not DirExists(Directory) then Exit;
  GetSystemTime(SystemTime);
  InstalledAt := Format('%.4d-%.2d-%.2dT%.2d:%.2d:%.2d.%.3dZ',
    [SystemTime.Year, SystemTime.Month, SystemTime.Day, SystemTime.Hour,
     SystemTime.Minute, SystemTime.Second, SystemTime.Milliseconds]);
  if FindFirst(AddBackslash(Directory) + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        begin
          Path := AddBackslash(Directory) + FindRec.Name;
          if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
            StampReviewedPackageRecords(Path)
          else if CompareText(FindRec.Name, 'installed.wsgm.json') = 0 then
          begin
            if not LoadStringFromFile(Path, Content) then
              RaiseException('Could not read reviewed package install grant: ' + Path);
            if StringChangeEx(Content,
                '1970-01-01T00:00:00+00:00', InstalledAt, True) <> 1 then
              RaiseException('Reviewed package install grant timestamp marker is invalid: ' + Path);
            if not SaveStringToFile(Path, Content, False) then
              RaiseException('Could not stamp reviewed package install grant: ' + Path);
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and WizardIsComponentSelected('device') then
    StampReviewedPackageRecords(
      ExpandConstant('{autopf}\WSGM\DevicePlugins\reviewed'));
end;

// WSGM is almost certainly running during an update (it IS the shell), and it
// may be ELEVATED. This setup is itself elevated (PrivilegesRequired=admin), so
// its token carries BUILTIN\Administrators and this user's SID — both of which
// the event's DACL grants EVENT_MODIFY_STATE. WSGM listens on a named
// MANUAL-RESET event (one SetEvent releases every waiting instance, elevated or
// not) and exits itself gracefully (which also asks Steam to exit and releases
// the injected Steam Input payload). taskkill remains as fallback for any
// leftovers. Returns True when the event existed, i.e. at least one WSGM
// instance was running.
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

procedure StopLaunchWrappers();
var
  R: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.Launch.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  // Pre-1.4 wrappers: an update must release these before [InstallDelete] can
  // remove them, or the stale exe survives and keeps old launch options alive.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM WSGM.Deelevate.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM steam-input-lease.exe /T /F', '', SW_HIDE, ewWaitUntilTerminated, R);
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
  // Releases the new wrapper executable too. /T also stops its helper and
  // launched target; setup already shuts Steam down before this point.
  StopLaunchWrappers();
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
  // Force-closing Steam is only justified when a WSGM instance was actually
  // running: only then is there an injected gate to unload and a steam://exit
  // for Steam to comply with. On a fresh install (or with WSGM not running) the
  // /T kill would take a running game's process tree down with no save prompt,
  // for a five-second wait that buys nothing. The wrapper cleanup stays
  // unconditional — [Files]/[InstallDelete] need those binaries unlocked.
  if WasRunning then
    StopSteamForUpdate()
  else
    StopLaunchWrappers();
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
  StopLaunchWrappers();
  Result := True;
end;
