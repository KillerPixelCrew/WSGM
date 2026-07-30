; OpenFSE installer — per-user, no admin rights, no UAC prompt.
; Build via build.ps1 (publishes the app first, then compiles this).

#define AppName "OpenFSE"
#define AppVersion "0.1.0"
#define AppPublisher "NightHammer1000"
#define AppURL "https://github.com/NightHammer1000/OpenFSE"
#define PublishDir "..\publish"

[Setup]
AppId={{9B7B5C63-1B7A-4A57-9E0D-0F3B7B1C9A11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppSupportURL={#AppURL}
; Per-user install: matches the app's own layout (%LOCALAPPDATA%\OpenFSE\bin)
DefaultDirName={localappdata}\OpenFSE\bin
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish
OutputBaseFilename=OpenFSE-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\OpenFSE.exe
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Files]
Source: "{#PublishDir}\OpenFSE.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\*.dll"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\OpenFSE.exe"; Comment: "OpenFSE settings"

[Run]
Filename: "{app}\OpenFSE.exe"; Description: "Open {#AppName} settings"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Restore the previous Windows shell BEFORE files are removed — otherwise the next
; logon would point at a deleted exe. Quiet: no explorer start, no UI.
Filename: "{app}\OpenFSE.exe"; Parameters: "--unregister-shell"; RunOnceId: "UnregisterShell"; Flags: runhidden

[UninstallDelete]
; Config/logs live one level up; remove them with the app (per-user data only).
Type: filesandordirs; Name: "{localappdata}\OpenFSE"
