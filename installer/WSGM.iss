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
; Restart Manager must not close the fixed-purpose recovery owner before [Code] observes its
; recovery acknowledgement. The installer retires this one image itself after that boundary.
CloseApplicationsFilterExcludes=WSGM.ShellAnchor.exe
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
Name: "device"; Description: "Device Integration runtime and one installed device package (remains disabled until enabled in WSGM Settings)"; Types: full
Name: "devicelab"; Description: "Device Lab and offline device-development tools"; Types: custom

[CustomMessages]
english.SteamMissing=Steam was not found on this PC.%n%nWSGM is Steam-exclusive and boots straight into Steam Big Picture. Install Steam from steampowered.com, sign in once, and then run this setup again.
german.SteamMissing=Steam wurde auf diesem PC nicht gefunden.%n%nWSGM funktioniert ausschließlich mit Steam und startet direkt in Steam Big Picture. Installiere Steam von steampowered.com, melde dich einmal an und führe dieses Setup danach erneut aus.

[Files]
; Only the NativeAOT app component is visible to this installer section. DeviceHost,
; Device Lab, and plugin packages are staged in sibling component directories, so
; the legacy DLL glob below cannot accidentally flatten JIT/plugin dependencies into {app}.
Source: "{#AppPublishDir}\WSGM.exe"; DestDir: "{app}"; Flags: ignoreversion
; The fixed-purpose Explorer recovery owner is the same AOT payload under a distinct process image.
; Installer force fallback can therefore end WSGM.exe without killing the owner that must restore
; Explorer. If its bounded recovery acknowledgement is unavailable, an interactive operation may
; defer replacement/deletion to reboot; a silent update keeps the old image and never auto-reboots.
Source: "{#AppPublishDir}\WSGM.exe"; DestDir: "{app}"; DestName: "WSGM.ShellAnchor.exe"; Flags: ignoreversion restartreplace uninsrestartdelete; Check: CanInstallShellAnchor
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
; WSGM's authoritative GPL-3.0-or-later license, staged from the repository root.
Source: "{#AppPublishDir}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; Third-party license texts for managed packages (src\WSGM\Licenses\).
Source: "{#AppPublishDir}\LoadingIndicators.Avalonia-UNLICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
; DeviceHost and the one plugin package are administrator-protected. The package
; is explicit trusted hardware code and never loads from a user-writable path.
; This glob also carries the exact self-contained .NET runtime license/notices
; asserted by eng\assert-component-staging.ps1 into every DeviceHost install.
Source: "{#DeviceHostPublishDir}\*"; DestDir: "{autopf}\WSGM\DeviceHost"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device
Source: "{#DevicePackagesPublishDir}\*"; DestDir: "{autopf}\WSGM\DevicePlugins\.staging"; Flags: ignoreversion recursesubdirs createallsubdirs; Components: device
; Device Lab never owns the production cycle and remains an explicit custom
; component. Its attended hardware actions still require interactive consent;
; its tool tree carries the exact self-contained .NET runtime license/notices.
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
; Self-contained component publishes must replace their complete previous closure.
; Overlaying would retain deleted assemblies and the retired CommandLine/ProbeHost tools.
; PrepareToInstall has already stopped WSGM and its DeviceHost child before this runs.
Type: filesandordirs; Name: "{autopf}\WSGM\DeviceHost"
Type: filesandordirs; Name: "{app}\Tools"
; Package bytes stage outside runtime discovery. CurStepChanged swaps this whole
; sibling into the sole installed slot after every file has landed successfully.
Type: filesandordirs; Name: "{autopf}\WSGM\DevicePlugins\.staging"
; Remove the per-user staging helper left by service-based preview builds.
Type: files; Name: "{app}\WSGM.LogonService.exe"
; The two wrappers WSGM.Launch.exe replaces. Deleting them is deliberate: a stale
; helper would keep an old pasted launch option working, so the two would drift
; apart silently. Gone, the option fails visibly and the release note explains it.
Type: files; Name: "{app}\WSGM.Deelevate.exe"
Type: files; Name: "{app}\steam-input-lease.exe"

[Code]
const
  ErrorAlreadyExists = 183;
  ErrorFileNotFound = 2;
  ErrorNoMoreFiles = 18;
  ErrorPathNotFound = 3;
  ErrorServiceDoesNotExist = 1060;
  FileAttributeDirectory = $00000010;
  FileAttributeReparsePoint = $00000400;
  InvalidFileAttributes = $FFFFFFFF;
  InvalidHandleValue = $FFFFFFFF;
  LabelSecurityInformation = $00000010;
  ScManagerConnect = $0001;
  SddlRevision1 = 1;
  ServiceQueryStatus = $0004;
  ServiceRunning = $00000004;
  ServiceStopped = $00000001;
  Th32csSnapProcess = $00000002;
  WaitAbandoned = $00000080;
  WaitObject0 = $00000000;

type
  TShutdownHandoffResult = (
    shrLegacy,
    shrCompleted,
    shrTimedOut,
    shrFailed);

  TProcessEntry32 = record
    Size: LongWord;
    Usage: LongWord;
    ProcessId: LongWord;
    DefaultHeapId: LongWord;
    ModuleId: LongWord;
    ThreadCount: LongWord;
    ParentProcessId: LongWord;
    PriorityClassBase: Integer;
    Flags: LongWord;
    ExeFile: array[0..259] of Char;
  end;

  TWin32FindData = record
    FileAttributes: LongWord;
    CreationTimeLow: LongWord;
    CreationTimeHigh: LongWord;
    LastAccessTimeLow: LongWord;
    LastAccessTimeHigh: LongWord;
    LastWriteTimeLow: LongWord;
    LastWriteTimeHigh: LongWord;
    FileSizeHigh: LongWord;
    FileSizeLow: LongWord;
    Reserved0: LongWord;
    Reserved1: LongWord;
    FileName: array[0..259] of Char;
    AlternateFileName: array[0..13] of Char;
  end;

  TServiceStatus = record
    ServiceType: LongWord;
    CurrentState: LongWord;
    ControlsAccepted: LongWord;
    Win32ExitCode: LongWord;
    ServiceSpecificExitCode: LongWord;
    CheckPoint: LongWord;
    WaitHint: LongWord;
  end;

var
  DeviceOwnerHandle: THandle;
  DeviceOwnerReservedForMutation: Boolean;
  DevicePackageGateHandle: THandle;
  DevicePackageGateOwned: Boolean;
  ShellAnchorReplacementSafe: Boolean;
  SetupDeviceHostStateVerified: Boolean;
  SetupInstallStarted: Boolean;
  SetupRuntimeClassificationCaptured: Boolean;
  SetupServiceExisted: Boolean;
  SetupServiceStateCaptured: Boolean;
  SetupServiceWasRunning: Boolean;
  SetupShutdownApplied: Boolean;
  RollbackOwnerRetentionAcknowledged: Boolean;
  UninstallDeviceHostStateVerified: Boolean;
  UninstallMutationStarted: Boolean;
  UninstallServiceExisted: Boolean;
  UninstallServiceWasRunning: Boolean;
  UninstallShutdownApplied: Boolean;
  UninstallWasRunning: Boolean;
  UninstallWasShell: Boolean;
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
  DeviceOwnerHandle := 0;
  DeviceOwnerReservedForMutation := False;
  DevicePackageGateHandle := 0;
  DevicePackageGateOwned := False;
  ShellAnchorReplacementSafe := True;
  SetupDeviceHostStateVerified := False;
  SetupInstallStarted := False;
  SetupRuntimeClassificationCaptured := False;
  SetupServiceExisted := False;
  SetupServiceStateCaptured := False;
  SetupServiceWasRunning := False;
  SetupShutdownApplied := False;
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
function OpenSCManagerW(lpMachineName, lpDatabaseName,
  dwDesiredAccess: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(hSCManager: THandle; lpServiceName: String;
  dwDesiredAccess: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function CreateEventW(lpEventAttributes: LongWord; bManualReset, bInitialState: BOOL;
  lpName: String): THandle;
  external 'CreateEventW@kernel32.dll stdcall';
function ConvertStringSecurityDescriptorToSecurityDescriptorW(
  StringSecurityDescriptor: String; StringSDRevision: LongWord;
  var SecurityDescriptor: LongWord; SecurityDescriptorSize: LongWord): BOOL;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function CreateMutexW(lpMutexAttributes: LongWord; bInitialOwner: BOOL;
  lpName: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function ReleaseMutexK(hMutex: THandle): BOOL;
  external 'ReleaseMutex@kernel32.dll stdcall';
function GetLastErrorK(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';
function GetFileAttributesW(lpFileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function FindFirstFileW(lpFileName: String;
  var FindFileData: TWin32FindData): THandle;
  external 'FindFirstFileW@kernel32.dll stdcall';
function FindNextFileW(hFindFile: THandle;
  var FindFileData: TWin32FindData): BOOL;
  external 'FindNextFileW@kernel32.dll stdcall';
function FindCloseK(hFindFile: THandle): BOOL;
  external 'FindClose@kernel32.dll stdcall';
function CreateToolhelp32SnapshotK(dwFlags, th32ProcessID: LongWord): THandle;
  external 'CreateToolhelp32Snapshot@kernel32.dll stdcall';
function Process32FirstW(hSnapshot: THandle; var Entry: TProcessEntry32): BOOL;
  external 'Process32FirstW@kernel32.dll stdcall';
function Process32NextW(hSnapshot: THandle; var Entry: TProcessEntry32): BOOL;
  external 'Process32NextW@kernel32.dll stdcall';
function QueryServiceStatusK(hService: THandle;
  var ServiceStatus: TServiceStatus): BOOL;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function SetKernelObjectSecurityK(Handle: THandle; SecurityInformation,
  SecurityDescriptor: LongWord): BOOL;
  external 'SetKernelObjectSecurity@advapi32.dll stdcall';
function SetEvent(hEvent: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function ResetEventK(hEvent: THandle): BOOL;
  external 'ResetEvent@kernel32.dll stdcall';
function WaitForSingleObjectK(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandleK(hObject: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function CloseServiceHandleK(hSCObject: THandle): BOOL;
  external 'CloseServiceHandle@advapi32.dll stdcall';
function LocalFreeK(Memory: LongWord): LongWord;
  external 'LocalFree@kernel32.dll stdcall';
function GetCurrentProcessIdK(): LongWord;
  external 'GetCurrentProcessId@kernel32.dll stdcall';
function ProcessIdToSessionIdK(dwProcessId: LongWord;
  var pSessionId: LongWord): BOOL;
  external 'ProcessIdToSessionId@kernel32.dll stdcall';

function AcquireDevicePackageSlotGate(): Boolean;
var
  WaitResult: LongWord;
begin
  if DevicePackageGateHandle <> 0 then
  begin
    Result := DevicePackageGateOwned;
    Exit;
  end;

  Result := False;
  DevicePackageGateHandle := CreateMutexW(
    0, False, 'Global\WSGM.DevicePackageSlot');
  if DevicePackageGateHandle = 0 then
  begin
    Log('Could not open the machine-wide Device Plugin package-slot gate');
    Exit;
  end;

  // Setup and uninstall lifecycle hooks execute on their main script thread. Keep mutex ownership
  // there from the stop/recheck boundary through package mutation; the matching deinitializer
  // closes an abandoned handle on every early-exit path.
  WaitResult := WaitForSingleObjectK(DevicePackageGateHandle, 5000);
  if (WaitResult = WaitObject0) or (WaitResult = WaitAbandoned) then
  begin
    DevicePackageGateOwned := True;
    Result := True;
    Exit;
  end;

  Log('The machine-wide Device Plugin package-slot gate remained busy or inaccessible');
  CloseHandleK(DevicePackageGateHandle);
  DevicePackageGateHandle := 0;
end;

procedure ReleaseDeviceOwnerReservation();
begin
  if DeviceOwnerHandle <> 0 then
  begin
    CloseHandleK(DeviceOwnerHandle);
    DeviceOwnerHandle := 0;
    DeviceOwnerReservedForMutation := False;
  end;
end;

procedure ReleaseDevicePackageGateReservation();
begin
  if DevicePackageGateHandle <> 0 then
  begin
    if DevicePackageGateOwned then
    begin
      if not ReleaseMutexK(DevicePackageGateHandle) then
        Log('Could not release the Device Plugin package-slot mutex cleanly');
      DevicePackageGateOwned := False;
    end;
    CloseHandleK(DevicePackageGateHandle);
    DevicePackageGateHandle := 0;
  end;
end;

procedure ReleaseDevicePublicationReservations();
begin
  // Close the unowned marker first. A new WSGM coordinator may reserve it, but it still has to
  // wait for the package gate until the completed publication is visible.
  ReleaseDeviceOwnerReservation();
  ReleaseDevicePackageGateReservation();
end;

function ReserveDeviceOwner(): Boolean;
var
  CreationError: LongWord;
begin
  if DeviceOwnerHandle <> 0 then
  begin
    if DeviceOwnerReservedForMutation then
    begin
      Result := True;
      Exit;
    end;

    // A previous refusal retained somebody else's marker while restoring WSGM. Under the package
    // gate, drop that observation and create again so a retry proves whether another handle remains.
    ReleaseDeviceOwnerReservation();
  end;

  Result := False;
  DeviceOwnerHandle := CreateMutexW(0, False, 'Global\WSGM.DeviceOwner');
  CreationError := GetLastErrorK();
  if DeviceOwnerHandle = 0 then
  begin
    Log('Could not create the machine-wide Device Plugin owner marker');
    Exit;
  end;
  if CreationError = ErrorAlreadyExists then
  begin
    Log('A machine-wide WSGM or Device Lab owner is still active');
    // Keep this unowned handle across rollback startup. It cannot authorize mutation, but it closes
    // the gap in which the existing owner could exit before the restored process retains the object.
    Exit;
  end;

  // WSGM elects ownership by object creation. Keep this handle open but never wait on or release
  // the mutex, so the reservation has no thread affinity during setup or uninstall mutation.
  DeviceOwnerReservedForMutation := True;
  Result := True;
end;

function ProcessEntryImage(const Entry: TProcessEntry32): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to 259 do
  begin
    if Entry.ExeFile[I] = #0 then Exit;
    Result := Result + Entry.ExeFile[I];
  end;
end;

function VerifyNoDeviceHostProcesses(): Boolean;
var
  Entry: TProcessEntry32;
  EnumerationError: LongWord;
  HasEntry: Boolean;
  Snapshot: THandle;
begin
  Result := False;
  Snapshot := CreateToolhelp32SnapshotK(Th32csSnapProcess, 0);
  if Snapshot = InvalidHandleValue then
  begin
    Log('Could not snapshot running processes before Device Plugin mutation');
    Exit;
  end;

  try
    Entry.Size := SizeOf(Entry);
    HasEntry := Process32FirstW(Snapshot, Entry);
    while HasEntry do
    begin
      if CompareText(ProcessEntryImage(Entry), 'WSGM.DeviceHost.exe') = 0 then
      begin
        Log('A WSGM.DeviceHost process remains after WSGM shutdown');
        Exit;
      end;
      Entry.Size := SizeOf(Entry);
      HasEntry := Process32NextW(Snapshot, Entry);
    end;

    EnumerationError := GetLastErrorK();
    if EnumerationError <> ErrorNoMoreFiles then
    begin
      Log('DeviceHost process enumeration ended with error ' +
        IntToStr(EnumerationError));
      Exit;
    end;
    Result := True;
  finally
    CloseHandleK(Snapshot);
  end;
end;

function InspectDeviceDirectory(const Path, Description: String;
  var Exists: Boolean): Boolean;
var
  Attributes, InspectionError: LongWord;
begin
  Result := False;
  Exists := False;
  Attributes := GetFileAttributesW(Path);
  if Attributes = InvalidFileAttributes then
  begin
    InspectionError := GetLastErrorK();
    if (InspectionError = ErrorFileNotFound) or
      (InspectionError = ErrorPathNotFound) then
    begin
      Result := True;
      Exit;
    end;
    Log(Description + ' could not be inspected; error=' + IntToStr(InspectionError));
    Exit;
  end;

  if (Attributes and FileAttributeDirectory) = 0 then
  begin
    Log(Description + ' is not a directory: ' + Path);
    Exit;
  end;
  if (Attributes and FileAttributeReparsePoint) <> 0 then
  begin
    Log(Description + ' is a link/reparse point and will not be traversed: ' + Path);
    Exit;
  end;

  Exists := True;
  Result := True;
end;

function DeleteInspectedDeviceDirectory(const Path, Description: String;
  Exists: Boolean): Boolean;
begin
  Result := True;
  if not Exists then Exit;
  Result := DelTree(Path, True, True, True);
  if not Result then
    Log(Description + ' could not be removed: ' + Path);
end;

function FindDataName(const Data: TWin32FindData): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to 259 do
  begin
    if Data.FileName[I] = #0 then Exit;
    Result := Result + Data.FileName[I];
  end;
end;

procedure AppendPath(var Paths: TArrayOfString; const Path: String);
var
  Count: Integer;
begin
  Count := GetArrayLength(Paths);
  SetArrayLength(Paths, Count + 1);
  Paths[Count] := Path;
end;

function CleanupStaleDevicePluginStaging(): Boolean;
var
  EnumerationError: LongWord;
  FindData: TWin32FindData;
  FindHandle: THandle;
  FixedStaging, LegacyStaging, Root: String;
  FixedStagingExists, LegacyStagingExists, RootExists: Boolean;
  Index: Integer;
  LegacyStagingPaths: TArrayOfString;
begin
  Result := False;
  Root := ExpandConstant('{autopf}\WSGM\DevicePlugins');
  FixedStaging := AddBackslash(Root) + '.staging';
  if not InspectDeviceDirectory(
    Root, 'Device Plugin slot parent', RootExists) or
    not InspectDeviceDirectory(
      FixedStaging, 'Device Plugin staging root', FixedStagingExists) then Exit;

  if RootExists then
  begin
    FindHandle := FindFirstFileW(
      AddBackslash(Root) + '.installed.staging-*', FindData);
    if FindHandle = InvalidHandleValue then
    begin
      EnumerationError := GetLastErrorK();
      if (EnumerationError <> ErrorFileNotFound) and
        (EnumerationError <> ErrorPathNotFound) then
      begin
        Log('Legacy Device Plugin staging roots could not be enumerated; error=' +
          IntToStr(EnumerationError));
        Exit;
      end;
    end
    else
    begin
      try
        repeat
          LegacyStaging := AddBackslash(Root) + FindDataName(FindData);
          if not InspectDeviceDirectory(
            LegacyStaging, 'Legacy Device Plugin staging root',
            LegacyStagingExists) then Exit;
          if LegacyStagingExists then
            AppendPath(LegacyStagingPaths, LegacyStaging);
        until not FindNextFileW(FindHandle, FindData);
        EnumerationError := GetLastErrorK();
        if EnumerationError <> ErrorNoMoreFiles then
        begin
          Log('Legacy Device Plugin staging enumeration ended with error=' +
            IntToStr(EnumerationError));
          Exit;
        end;
      finally
        FindCloseK(FindHandle);
      end;
    end;
  end;

  // Every cleanup target and the parent were inspected before the first delete. Never let an
  // inaccessible later match turn a partially completed cleanup into a successful preflight.
  if not DeleteInspectedDeviceDirectory(
    FixedStaging, 'Device Plugin staging root', FixedStagingExists) then Exit;
  for Index := 0 to GetArrayLength(LegacyStagingPaths) - 1 do
    if not DeleteInspectedDeviceDirectory(
      LegacyStagingPaths[Index], 'Legacy Device Plugin staging root', True) then Exit;
  Result := True;
end;

procedure ReplaceDevicePluginSlot();
var
  HadInstalled: Boolean;
  Installed, LegacyPrevious, LegacyReviewed, Previous, Root, Staging: String;
  InstalledExists, LegacyPreviousExists, LegacyReviewedExists, PreviousExists,
    RootExists, StagingExists: Boolean;
begin
  Root := ExpandConstant('{autopf}\WSGM\DevicePlugins');
  Installed := AddBackslash(Root) + 'installed';
  Staging := AddBackslash(Root) + '.staging';
  Previous := AddBackslash(Root) + '.previous';
  LegacyPrevious := AddBackslash(Root) + '.installed.previous';
  LegacyReviewed := AddBackslash(Root) + 'reviewed';

  // Validate every move/delete target before changing any slot state. A top-level reparse point
  // is never followed by installer cleanup, even though the parent is administrator-protected.
  if not InspectDeviceDirectory(Root, 'Device Plugin slot parent', RootExists) or
    not InspectDeviceDirectory(Installed, 'Installed Device Plugin slot', InstalledExists) or
    not InspectDeviceDirectory(Staging, 'Device Plugin staging root', StagingExists) or
    not InspectDeviceDirectory(Previous, 'Device Plugin recovery root', PreviousExists) or
    not InspectDeviceDirectory(
      LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
    not InspectDeviceDirectory(
      LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) then
    RaiseException('The Device Plugin slot contains an unsafe or inaccessible path.');
  if not RootExists and (InstalledExists or StagingExists or PreviousExists or
    LegacyPreviousExists or LegacyReviewedExists) then
    RaiseException('Device Plugin slot children exist without a valid parent directory.');

  if not WizardIsComponentSelected('device') then
  begin
    // Component removal is authoritative: remove both recovery namespaces as well as the live
    // slot so no interrupted-update backup can resurrect Device Integration on next startup.
    // The live slot is deliberately last: a failed backup cleanup leaves the current package
    // installed instead of turning the surviving recovery directory into the next active slot.
    if not DeleteInspectedDeviceDirectory(
      Staging, 'Device Plugin staging root', StagingExists) or
      not DeleteInspectedDeviceDirectory(
        Previous, 'Device Plugin recovery root', PreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) or
      not DeleteInspectedDeviceDirectory(
        Installed, 'Installed Device Plugin slot', InstalledExists) then
      RaiseException('Could not remove every Device Plugin package and recovery root.');
    DelTree(ExpandConstant('{autopf}\WSGM\DeviceHost'), True, True, True);
    Exit;
  end;

  if not StagingExists then
    RaiseException('The one Device Plugin package was not staged.');

  if InstalledExists then
  begin
    // A live directory proves publication completed. Any recovery sibling is stale and may be
    // retired only now, after the replacement staging tree is complete.
    if not DeleteInspectedDeviceDirectory(
      Previous, 'Device Plugin recovery root', PreviousExists) or
      not DeleteInspectedDeviceDirectory(
        LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) then
      RaiseException('Could not retire stale Device Plugin recovery state.');
    PreviousExists := False;
    LegacyPreviousExists := False;
  end
  else
  begin
    if PreviousExists and LegacyPreviousExists then
      RaiseException('Both current and legacy Device Plugin recovery roots exist.');
    if LegacyPreviousExists then
    begin
      if not RenameFile(LegacyPrevious, Previous) then
        RaiseException('Could not migrate the legacy Device Plugin recovery root.');
      PreviousExists := True;
      LegacyPreviousExists := False;
    end;
  end;

  HadInstalled := InstalledExists;
  if HadInstalled and not RenameFile(Installed, Previous) then
    RaiseException('Could not move the previous Device Plugin outside the active slot.');
  if HadInstalled then
    PreviousExists := True;

  if not RenameFile(Staging, Installed) then
  begin
    if HadInstalled then RenameFile(Previous, Installed);
    RaiseException('Could not atomically install the replacement Device Plugin.');
  end;

  if not DeleteInspectedDeviceDirectory(
    Previous, 'Device Plugin recovery root', PreviousExists) or
    not DeleteInspectedDeviceDirectory(
      LegacyPrevious, 'Legacy Device Plugin recovery root', LegacyPreviousExists) or
    not DeleteInspectedDeviceDirectory(
      LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists) then
    RaiseException('The Device Plugin was published but stale package state could not be retired.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    SetupInstallStarted := True
  else if CurStep = ssPostInstall then
  begin
    try
      ReplaceDevicePluginSlot();
      // [Run] now owns service/runtime restart. Do not let DeinitializeSetup launch a second copy.
      SetupShutdownApplied := False;
    finally
      ReleaseDevicePublicationReservations();
    end;
  end;
end;

// WSGM is almost certainly running during an update (it IS the shell), and it
// may be ELEVATED. This setup is itself elevated (PrivilegesRequired=admin), so
// its token carries BUILTIN\Administrators and this user's SID — both of which
// the event's DACL grants EVENT_MODIFY_STATE. WSGM listens on a named
// MANUAL-RESET event (one SetEvent releases every waiting instance, elevated or
// not) and exits itself gracefully (which also asks Steam to exit and releases
// the injected Steam Input payload). taskkill remains as fallback for any
// leftovers. New builds expose one optional completion event and write the compact
// clean/unverified/timed-out/failed outcome to wsgm.log. Its absence identifies an
// old build and preserves the original bounded wait/taskkill fallback.
function RequestRunningInstancesExit(const EventName: String;
  GraceIterations: Integer;
  var HandoffResult: TShutdownHandoffResult): Boolean;
var
  I: Integer;
  H, CompletionEvent: THandle;
  HasHandoffChannel: Boolean;
  CompletionObserved: Boolean;
begin
  Result := False;
  HandoffResult := shrLegacy;
  CompletionObserved := False;
  CompletionEvent := OpenEventW(
    $00100002 { SYNCHRONIZE | EVENT_MODIFY_STATE },
    False, EventName + '.Completed');
  HasHandoffChannel := CompletionEvent <> 0;
  try
    if HasHandoffChannel then
      ResetEventK(CompletionEvent);

    H := OpenEventW($0002 { EVENT_MODIFY_STATE }, False, EventName);
    if H <> 0 then
    begin
      Result := True;
      if not SetEvent(H) then
      begin
        if HasHandoffChannel then
          HandoffResult := shrFailed;
      end
      else
      begin
        // Wait for both the shell owner to release its mutex and a new build to
        // publish completion. A settings-only instance has no shell mutex, so the
        // completion event prevents setup from force-stopping it immediately.
        for I := 1 to GraceIterations do
        begin
          if HasHandoffChannel and
            (WaitForSingleObjectK(CompletionEvent, 0) = 0) then
            CompletionObserved := True;
          if (not CheckForMutexes('WSGM.Shell')) and
            ((not HasHandoffChannel) or CompletionObserved) then Break;
          Sleep(500);
        end;
        Sleep(500);

        if HasHandoffChannel then
          if CompletionObserved and not CheckForMutexes('WSGM.Shell') then
            HandoffResult := shrCompleted
          else
            HandoffResult := shrTimedOut;
      end;
      CloseHandleK(H);
    end;
  finally
    if CompletionEvent <> 0 then CloseHandleK(CompletionEvent);
  end;
end;

procedure ForceStopCurrentSessionImage(const ImageName: String);
var
  Args: String;
  R: Integer;
  SessionId: LongWord;
begin
  // Every shutdown/recovery object above is Local to this Terminal Services session. Match that
  // ownership boundary when applying the force fallback: an image-only taskkill would also end
  // another logged-on user's primary and the anchor that is still restoring that user's desktop.
  if not ProcessIdToSessionIdK(GetCurrentProcessIdK(), SessionId) then
  begin
    Log('Could not resolve the installer session; refusing a cross-session force stop for ' +
      ImageName);
    Exit;
  end;
  Args := '/FI "SESSION eq ' + IntToStr(SessionId) + '" /IM "' + ImageName + '" /F';
  if not Exec(ExpandConstant('{sys}\taskkill.exe'), Args, '', SW_HIDE,
    ewWaitUntilTerminated, R) then
    Log('Could not start the current-session force stop for ' + ImageName);
end;

function CanInstallShellAnchor(): Boolean;
begin
  // restartreplace is an interactive fallback only. A silent update must never turn an
  // unacknowledged recovery owner into Inno's automatic reboot path.
  Result := ShellAnchorReplacementSafe or not WizardSilent();
end;

procedure LogShutdownHandoff(const Operation: String;
  HandoffResult: TShutdownHandoffResult);
begin
  case HandoffResult of
    shrCompleted:
      Log(Operation + ' shutdown handoff: completed; compact outcome is in wsgm.log');
    shrTimedOut:
      Log(Operation + ' shutdown handoff: timed out; applying bounded force-stop fallback');
    shrFailed:
      Log(Operation + ' shutdown handoff: failed; applying bounded force-stop fallback');
    shrLegacy:
      Log(Operation + ' shutdown handoff: legacy build without result channel; preserving fallback');
  end;
end;

procedure ForceStopRunningInstances();
begin
  // Fallback / leftovers (unelevated instances only — elevated ones should
  // already have exited through their bounded graceful path). Never cross the
  // Local event/mutex session boundary while stopping an identically named run.
  ForceStopCurrentSessionImage('WSGM.exe');
end;

procedure WaitForShellAnchorRecovery();
var
  AnchorPath: String;
  H: THandle;
  WaitResult: LongWord;
begin
  AnchorPath := ExpandConstant('{app}\WSGM.ShellAnchor.exe');
  // The companion signals only after an explicit stop or after owner-loss recovery has made its
  // preserve/restore decision. Never image-kill it before that boundary: it may be the only process
  // capable of restoring Explorer after the primary force fallback. Keep this event handle open
  // through retirement so a concurrent replacement anchor cannot reuse the name and enter this
  // session's image-name scope. A missing/late acknowledgement leaves the file to interactive restartreplace,
  // silent-update preservation, or uninsrestartdelete so installer completion remains bounded.
  H := OpenEventW(
    $00100000 { SYNCHRONIZE }, False,
    'Local\WSGM.ShellAnchor.RecoverySettled');
  if H = 0 then
  begin
    // No recovery owner advertised itself. Delete the old image now so [Files] cannot discover a
    // late lock and silently request a reboot; a concurrent pre-handshake anchor makes this fail.
    if FileExists(AnchorPath) and not DeleteFile(AnchorPath) then
    begin
      ShellAnchorReplacementSafe := False;
      Log('Shell-anchor image remained locked without a recovery event; silent update will preserve it');
    end;
    Exit;
  end;
  WaitResult := WaitForSingleObjectK(H, 5000);
  if WaitResult = 0 then
  begin
    // The acknowledgement is published from the companion's final exit path. Give natural process
    // exit a short head start, then retire only this session's companion before replacement.
    Sleep(250);
    ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe');
    if FileExists(AnchorPath) and not DeleteFile(AnchorPath) then
    begin
      ShellAnchorReplacementSafe := False;
      Log('Acknowledged shell-anchor image remained locked; silent update will preserve it');
    end;
  end
  else
  begin
    ShellAnchorReplacementSafe := False;
    Log('Shell-anchor recovery acknowledgement timed out; leaving the companion alive for ' +
      'deferred replacement/deletion');
  end;
  CloseHandleK(H);
end;

function StopRunningInstances(): Boolean;
var
  HandoffResult: TShutdownHandoffResult;
begin
  // WSGM first has a bounded 10-second Steam/wrapper pre-stop, then its own
  // 10-second update cleanup. Forty-four half-second iterations plus the final
  // settle leave margin for dispatcher handoff before the force-stop fallback.
  Result := RequestRunningInstancesExit(
    'Local\WSGM.ExitForUpdate', 44, HandoffResult);
  if Result then
    LogShutdownHandoff('Update', HandoffResult);
  ForceStopRunningInstances();
  WaitForShellAnchorRecovery();
end;

function StopRunningInstancesForUninstall(): Boolean;
var
  HandoffResult: TShutdownHandoffResult;
begin
  Result := False;
  // New builds expose a distinct 20-second uninstall budget. When removing an
  // older build, fall back to its cross-version update event and preserve enough
  // grace for that build's Steam pre-stop before applying the force fallback.
  if RequestRunningInstancesExit(
    'Local\WSGM.ExitForUninstall', 40, HandoffResult) then
  begin
    Result := True;
    LogShutdownHandoff('Uninstall', HandoffResult);
  end
  else if RequestRunningInstancesExit(
    'Local\WSGM.ExitForUpdate', 44, HandoffResult) then
  begin
    Result := True;
    LogShutdownHandoff('Uninstall through legacy update event', HandoffResult);
  end;
  ForceStopRunningInstances();
  WaitForShellAnchorRecovery();
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

function InspectLogonServiceState(var Exists, Running: Boolean): Boolean;
var
  Manager, Service: THandle;
  OpenError: LongWord;
  Status: TServiceStatus;
begin
  Result := False;
  Exists := False;
  Running := False;
  Manager := OpenSCManagerW(0, 0, ScManagerConnect);
  if Manager = 0 then
  begin
    Log('Could not open the Service Control Manager to inspect WSGMLogonService');
    Exit;
  end;

  try
    Service := OpenServiceW(Manager, 'WSGMLogonService', ServiceQueryStatus);
    if Service = 0 then
    begin
      OpenError := GetLastErrorK();
      if OpenError = ErrorServiceDoesNotExist then
        Result := True
      else
        Log('Could not inspect WSGMLogonService; error=' + IntToStr(OpenError));
      Exit;
    end;

    try
      Exists := True;
      if not QueryServiceStatusK(Service, Status) then
      begin
        Log('Could not query WSGMLogonService state; error=' +
          IntToStr(GetLastErrorK()));
        Exit;
      end;
      if Status.CurrentState = ServiceStopped then
      begin
        Result := True;
        Exit;
      end;
      if Status.CurrentState = ServiceRunning then
      begin
        Running := True;
        Result := True;
        Exit;
      end;

      // Pending or unsupported service states cannot be reproduced exactly after refusal. Stop no
      // process and fail before the runtime or protected files are touched.
      Log('WSGMLogonService is in an unverified transitional state: ' +
        IntToStr(Status.CurrentState));
    finally
      CloseServiceHandleK(Service);
    end;
  finally
    CloseServiceHandleK(Manager);
  end;
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

function ApplyMediumMandatoryLabelToEvent(const EventHandle: THandle;
  const Operation: String): Boolean;
var
  SecurityDescriptor: LongWord;
begin
  SecurityDescriptor := 0;
  Result := ConvertStringSecurityDescriptorToSecurityDescriptorW(
    'S:(ML;;NW;;;ME)', SddlRevision1, SecurityDescriptor, 0);
  if not Result then
  begin
    Log(Operation + ': could not build the medium-integrity event label');
    Exit;
  end;

  try
    // Keep CreateEventW's same-user DACL intact. Only lower the mandatory label so the restored
    // medium-integrity Settings process can signal this elevated installer's acknowledgement;
    // no-write-up still excludes low-integrity callers.
    Result := SetKernelObjectSecurityK(
      EventHandle, LabelSecurityInformation, SecurityDescriptor);
    if not Result then
      Log(Operation + ': could not apply the medium-integrity event label');
  finally
    LocalFreeK(SecurityDescriptor);
  end;
end;

function CreateRollbackOwnerAcknowledgementEvent(
  const Operation: String): THandle;
var
  CreationError: LongWord;
begin
  Result := CreateEventW(
    0, True, False, 'Local\WSGM.InstallerRollback.DeviceOwnerRetained');
  CreationError := GetLastErrorK();
  if Result = 0 then
  begin
    Log(Operation + ': could not create the rollback owner-retention acknowledgement');
    Exit;
  end;
  if CreationError = ErrorAlreadyExists then
  begin
    // Only this recovery attempt creates the channel. Refuse a pre-existing object rather than
    // inheriting its unknown DACL, reset mode, or a handle that can spoof acknowledgement.
    Log(Operation + ': rollback owner-retention acknowledgement already existed');
    CloseHandleK(Result);
    Result := 0;
    Exit;
  end;

  if not ApplyMediumMandatoryLabelToEvent(Result, Operation) then
  begin
    Log(Operation + ': could not secure the rollback owner-retention acknowledgement');
    CloseHandleK(Result);
    Result := 0;
    Exit;
  end;
  if not ResetEventK(Result) then
  begin
    Log(Operation + ': could not reset the rollback owner-retention acknowledgement');
    CloseHandleK(Result);
    Result := 0;
  end;
end;

procedure RestoreStoppedServiceAndRuntime(const Operation: String;
  ServiceExisted, ServiceWasRunning, RuntimeWasShell, RuntimeWasRunning,
  DeviceHostStateVerified: Boolean);
var
  Arguments, RuntimePath, ServicePath: String;
  ReadyEvent: THandle;
  R: Integer;
  WaitResult: LongWord;
begin
  RollbackOwnerRetentionAcknowledged := DeviceOwnerHandle = 0;
  // This rollback runs only before setup/uninstall file mutation. A verified restore is called after
  // both reservations are released. An unverified restore keeps the owner handle, releases only the
  // package gate, and carries the suppression/handle-retention handshake below.
  if ServiceExisted and ServiceWasRunning then
  begin
    // Use the service's installer path rather than `sc start`: --install tags the SCM start so a
    // recent autologon cannot be mistaken for a missed logon and trigger a second --boot takeover.
    ServicePath := ExpandConstant('{autopf}\WSGM\WSGM.LogonService.exe');
    if not FileExists(ServicePath) then
      Log(Operation + ': the previous logon-service executable is absent; service restart was skipped')
    else if not Exec(ServicePath, '--install', '', SW_HIDE, ewWaitUntilTerminated, R) then
      Log(Operation + ': could not run the logon-service recovery command')
    else if R <> 0 then
      Log(Operation + ': logon-service recovery exited with code ' + IntToStr(R));
  end;

  if not RuntimeWasRunning then Exit;
  RuntimePath := ExpandConstant('{app}\WSGM.exe');
  if not FileExists(RuntimePath) then
  begin
    Log(Operation + ': the previous WSGM executable is absent; runtime restart was skipped');
    Exit;
  end;
  if RuntimeWasShell then
  begin
    Arguments := '--shell';
  end
  else
    // Do not re-run legacy auto-mode classification during rollback. The initially observed
    // settings process must come back as settings even if Explorer recovery is still settling.
    Arguments := '--settings';
  if not DeviceHostStateVerified then
    Arguments := Arguments + ' --installer-rollback-no-device';

  ReadyEvent := 0;
  if not DeviceHostStateVerified and (DeviceOwnerHandle <> 0) then
    ReadyEvent := CreateRollbackOwnerAcknowledgementEvent(Operation);

  if not Exec(RuntimePath, Arguments, '', SW_SHOWNORMAL, ewNoWait, R) then
    Log(Operation + ': could not restart the previous WSGM runtime');
  if ReadyEvent <> 0 then
  begin
    WaitResult := WaitForSingleObjectK(ReadyEvent, 5000);
    RollbackOwnerRetentionAcknowledged := WaitResult = WaitObject0;
    if not RollbackOwnerRetentionAcknowledged then
      Log(Operation + ': rollback runtime did not retain the owner marker; keeping the installer reservation');
    CloseHandleK(ReadyEvent);
  end;
end;

procedure RestoreStoppedSetupRuntime();
begin
  if not SetupShutdownApplied then Exit;
  // Clear first so a failed best-effort launch cannot be duplicated by DeinitializeSetup.
  SetupShutdownApplied := False;
  RestoreStoppedServiceAndRuntime(
    'Setup rollback', SetupServiceExisted, SetupServiceWasRunning,
    WasShell, WasRunning, SetupDeviceHostStateVerified);
end;

procedure RestoreStoppedUninstallRuntime();
begin
  if not UninstallShutdownApplied then Exit;
  UninstallShutdownApplied := False;
  RestoreStoppedServiceAndRuntime(
    'Uninstall rollback', UninstallServiceExisted, UninstallServiceWasRunning,
    UninstallWasShell, UninstallWasRunning, UninstallDeviceHostStateVerified);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not AcquireDevicePackageSlotGate() then
  begin
    Result := 'The protected Device Plugin slot is busy or could not be verified. ' +
      'Close WSGM maintenance and try setup again.';
    Exit;
  end;

  if not SetupServiceStateCaptured then
  begin
    if not InspectLogonServiceState(SetupServiceExisted, SetupServiceWasRunning) then
    begin
      ReleaseDevicePackageGateReservation();
      Result := 'The WSGM logon-service state could not be verified. ' +
        'Setup refused to stop the current session.';
      Exit;
    end;
    SetupServiceStateCaptured := True;
  end;
  if SetupServiceWasRunning then
    StopLogonService();
  // Capture the initial mode exactly once. A post-shutdown refusal restores that mode, and a retry
  // stops it again without overwriting the classification with the temporary stopped state.
  if not SetupRuntimeClassificationCaptured then
  begin
    // Only the shell-mode instance holds this mutex (session namespace). taskkill's exit code is
    // deliberately NOT part of WasRunning: image-name fallback also sees unrelated portable runs.
    WasShell := CheckForMutexes('WSGM.Shell');
    WasRunning := StopRunningInstances() or WasShell;
    SetupRuntimeClassificationCaptured := True;
  end
  else if StopRunningInstances() then
    Log('Setup retry stopped the previously restored WSGM runtime');
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
  SetupShutdownApplied := True;
  SetupDeviceHostStateVerified := False;

  // The package gate has prevented any new host startup since before shutdown. Atomically reserve
  // the exact global hardware-owner marker, then prove that no old or orphaned DeviceHost remains.
  // Both handles stay live through [InstallDelete], [Files] staging, and the post-install swap.
  if not ReserveDeviceOwner() then
  begin
    ReleaseDevicePackageGateReservation();
    RestoreStoppedSetupRuntime();
    if RollbackOwnerRetentionAcknowledged then
      ReleaseDeviceOwnerReservation();
    Result := 'A WSGM or Device Lab hardware owner is still active on this machine. ' +
      'Close it and try setup again.';
    Exit;
  end;
  if not VerifyNoDeviceHostProcesses() then
  begin
    // Let the rollback runtime start while this process still owns the machine marker. A current
    // WSGM opens the same unowned object and acknowledges its process-lifetime retention before this
    // handle closes; an older/non-starting build leaves this installer as the fail-closed owner.
    ReleaseDevicePackageGateReservation();
    RestoreStoppedSetupRuntime();
    if RollbackOwnerRetentionAcknowledged then
      ReleaseDeviceOwnerReservation();
    Result := 'A DeviceHost process is still running, or its state could not be verified. ' +
      'Setup refused to replace device files.';
    Exit;
  end;
  SetupDeviceHostStateVerified := True;
  if not CleanupStaleDevicePluginStaging() then
  begin
    ReleaseDevicePublicationReservations();
    RestoreStoppedSetupRuntime();
    Result := 'Stale Device Plugin staging could not be removed safely. ' +
      'Setup refused to replace the protected slot.';
    Exit;
  end;
end;

procedure DeinitializeSetup();
begin
  // Covers cancellation and failures before ssPostInstall. Closing an owned gate handle after a
  // release failure makes it abandoned rather than leaving replacement blocked indefinitely.
  ReleaseDevicePublicationReservations();
  if not SetupInstallStarted then
    RestoreStoppedSetupRuntime();
end;

// The uninstaller must stop a running WSGM too (in desktop mode — the only
// place Settings > Apps > Uninstall is reachable — WSGM stays resident), or
// WSGM.exe stays locked, file removal leaves 'could not be removed' leftovers
// and a zombie ex-shell process keeps running.
function InitializeUninstall(): Boolean;
begin
  DeviceOwnerHandle := 0;
  DeviceOwnerReservedForMutation := False;
  DevicePackageGateHandle := 0;
  DevicePackageGateOwned := False;
  UninstallDeviceHostStateVerified := False;
  UninstallMutationStarted := False;
  UninstallServiceExisted := False;
  UninstallServiceWasRunning := False;
  UninstallShutdownApplied := False;
  Result := False;
  if not AcquireDevicePackageSlotGate() then
  begin
    MsgBox('The protected Device Plugin slot is busy or could not be verified. ' +
      'Close WSGM maintenance and try uninstall again.', mbCriticalError, MB_OK);
    Exit;
  end;

  if not InspectLogonServiceState(
    UninstallServiceExisted, UninstallServiceWasRunning) then
  begin
    ReleaseDevicePackageGateReservation();
    MsgBox('The WSGM logon-service state could not be verified. ' +
      'Uninstall refused to stop the current session.', mbCriticalError, MB_OK);
    Exit;
  end;
  if UninstallServiceWasRunning then
    StopLogonService();
  UninstallWasShell := CheckForMutexes('WSGM.Shell');
  UninstallWasRunning := StopRunningInstancesForUninstall() or UninstallWasShell;
  StopLaunchWrappers();
  UninstallShutdownApplied := True;
  if not ReserveDeviceOwner() then
  begin
    ReleaseDevicePackageGateReservation();
    RestoreStoppedUninstallRuntime();
    if RollbackOwnerRetentionAcknowledged then
      ReleaseDeviceOwnerReservation();
    MsgBox('A WSGM or Device Lab hardware owner is still active on this machine. ' +
      'Close it and try uninstall again.', mbCriticalError, MB_OK);
    Exit;
  end;
  if not VerifyNoDeviceHostProcesses() then
  begin
    ReleaseDevicePackageGateReservation();
    RestoreStoppedUninstallRuntime();
    if RollbackOwnerRetentionAcknowledged then
      ReleaseDeviceOwnerReservation();
    MsgBox('A DeviceHost process is still running, or its state could not be verified. ' +
      'Uninstall refused to remove device files.', mbCriticalError, MB_OK);
    Exit;
  end;
  UninstallDeviceHostStateVerified := True;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    UninstallMutationStarted := True;
end;

procedure DeinitializeUninstall();
begin
  // [UninstallRun] and [UninstallDelete] complete before this callback. Keep the exact global gate
  // and owner marker live through both, then release on the same script thread that acquired them.
  ReleaseDevicePublicationReservations();
  if not UninstallMutationStarted then
    RestoreStoppedUninstallRuntime();
end;
