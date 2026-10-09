# Install, update and uninstall

What `WSGM.Setup.exe` does to a machine: where the product lives, what a fresh install chooses and
asks, the order an install and an update run in, the daily update check, the uninstaller, and the
two takeovers setup performs on the user's behalf. The sign-in start, the Explorer takeover and the
mode transitions setup restarts are in [boot and shell](boot-and-shell.md); why WSGM is elevated at
all is in [decisions](decisions.md).

`WSGM.Setup.exe` (`src\WSGM.Setup`) is one elevated process, because the logon service and the
drivers demand it. The product lives under `%ProgramFiles%\WSGM`: `App` holds WSGM and its
executables, `Plugins` the installed `.wsgmpkg` files, and `Setup` a copy of the setup with its
bundled packages, so repair and uninstall work offline and when WSGM itself cannot start. User state
stays per-user in `%LOCALAPPDATA%\WSGM` and HKCU, which belong to the elevating account; this is the
single-user-device design. Setup keeps its own machine records in `%ProgramData%\WSGM`:
`components.json` (the drivers it installed), `bundle.json` (the installed bundle) and `setup.log`.

The setup carries everything: the application, the controller stack (VIIPER, the USB/IP driver,
HidHide) and every bundled plugin. The one thing it downloads is RivaTuner Statistics Server, only
when the RTSS switch is on and RTSS is missing; offline, that step fails on its own and the rest of
the install completes (see [RTSS integration](rtss.md#boundary)). The approved screen design is
`src\WSGM.Setup\docs\mockup.html`.

## What gets installed

Setup reads the machine identity (SMBIOS, CPU) and matches it against the hardware rules of every
bundled device plugin (`PluginOffers`). A match installs that plugin and switches Device Integration
on; exact rules rank above fallbacks, and remaining ties are shown for the user to pick one. No
match installs plain WSGM with integration off. The installed plugin's declared capabilities decide
the components (`SetupComponents`): a controller role (`ControllerSource`, `MotionSource`,
`HapticSink`) brings VIIPER, USB/IP and HidHide. Bundled common plugins are offered as checkboxes.

Intel, AMD and NVIDIA driver support ships in `App` through the directly referenced
`LibGPUDriverInteract` library. It works independently of the device plugin selection and needs no
GPU package installation. WSGM discovers the installed drivers and exposes their supported controls
through the existing overlay and Steam graphics surfaces.

An install, update or repair retires installed archives whose root `plugin.wsgm.json` declares
exactly `wsgm.gpu.intel`, `wsgm.gpu.amd` or `wsgm.gpu.nvidia`. It reads the archive identity rather
than relying on the filename. The existing setup file transaction backs those archives up and
restores them if the installation rolls back. Other packages and per-user `PluginState` recovery
records stay in place; the direct GPU integration retains the existing vendor journal identities.

The Profile page asks Full or Minimal, Steam first or Desktop first, start at sign-in and consent to
take over Steam's autostart. Customize is a separate following step in installs, updates and
repairs, with a description for each integration switch. A fresh install starts with Full when a
device plugin is selected and Minimal without one, until the user explicitly chooses a level or
edits a switch. Setup passes the choices as setup answers (`--setup --answers=<file>`,
`Core\SetupAnswers.cs`). An update or repair starts from the values WSGM exports
(`--export-setup-answers`), so it never silently undoes Settings. A quiet fresh install never takes
over Steam's autostart; a quiet update keeps an accepted takeover. Quick Setup is retired, and WSGM
Settings changes the choices afterwards.

On a fresh installation, selecting Device Integration also enables controller management so the
installed device plugin can supply controller input. Updates and repairs preserve the saved
controller-management choice. Setup logs the resulting value alongside the applied answers.

Full also turns on RTSS performance controls (`Performance.Enabled`), and setup installs RTSS when
none is registered. Minimal leaves the switch off. A quiet fresh install keeps WSGM's default, which
is off, so it downloads nothing.

A WSGM 1.0 install is removed first through its own Inno uninstaller (`/VERYSILENT`), and setup
stops when that fails. Nothing from 1.0 is carried over. That uninstaller signals the uninstall
event, on which WSGM leaves Steam running, so setup first stops WSGM through the update event: WSGM
then closes Steam gracefully, as the old installer's update did, and setup records the mode it ran
in.

## The Steam autostart takeover

WSGM starts Steam so the client inherits WSGM's integrity, and a Steam that Windows started first
takes that away without saying so. `Core\SteamAutostart` therefore looks for the places Windows
would start it: `Run` values in HKCU and HKLM including the 32-bit view, shortcuts in either Startup
folder resolved through `Interop\ShellLink`, and scheduled tasks with a logon trigger. Tasks are
read as language-neutral XML from `schtasks /Query /XML`, because the table output is localized.
Matching compares the resolved executable against Steam's own path, and an unquoted command is
resolved the way Windows resolves it, by successive prefixes rather than the first space.

`Core\SteamAutostartTakeover` disables an entry the way Task Manager's Startup tab does, by writing
Windows' own `StartupApproved` bytes, or by disabling the task. Nothing is deleted. The previous
state is recorded in `SteamAutostartDisabled` before the write, so an interrupted takeover is still
undoable. A recording failure refuses the write. Task disable trusts the command's exit code;
approval writes record their exact marker bytes once and trust the accepted write, with no
confirming read. Recording or write failures stay pending. Restore only undoes an entry that still
carries WSGM's own value, so a decision the user made afterwards always wins. HKLM and task changes
need elevation and go through the `--disable-steam-autostart` one-shot, which rescans and takes no
name from its command line. A sign-in never prompts: an unelevated re-check disables user-scope
entries and warns about the rest. `--restore-steam-autostart` runs from the elevated uninstall
restore.

Setup asks the sign-in choices and, through `WSGM.exe --export-setup-answers`, lists the enabled
entries the read-only scan found; the takeover is its own consent line on the profile page. When the
user consents, `WSGM.exe --setup --answers=<file>` records the choice and disables the entries in
the same elevated run. A silent fresh install never consents; a silent update keeps the existing
answer. Every WSGM start re-checks for entries that came back. Settings > System shows the state and
offers "Take over again", which scans and applies on a worker and disables itself until the
operation finishes.

## The other handheld managers

Full mode also turns off the other handheld managers, so WSGM is the only one driving the device:
Handheld Companion, found by the logon task that runs it, and the maker's apps from Handheld
Companion's own OEM lists, which are MSI Center M, Armoury Crate, Legion Space and the Zotac
launcher (`Core\OtherManagers`). Their services are set to disabled and stopped, their tasks
disabled, and a running window is asked to close but never ended. Each change is recorded in
`config.json` before it is made, and `--uninstall-restore` puts back the exact start types and
tasks. The profile page names what was found, Customize has the switch, Minimal leaves them alone,
and a quiet fresh install never turns them off.

Once accepted, the takeover is kept, not applied once. Handheld Companion's uninstaller re-enables
the maker's services it had turned off, and on an Ally the Armoury Crate helper then answers the
Armoury Crate button with a dialog asking to install Armoury Crate SE. Every shell start therefore
detects again on a worker and turns off what came back. That re-check never prompts, so an
unelevated WSGM only logs what it found. Settings > System shows what was recorded and offers "Take
over again", which detects on a worker and applies from the Settings process when it is elevated, or
through the `--disable-other-managers` one-shot with one prompt otherwise. Pressed before the
takeover was accepted, it names what it found and asks to save first, and a save with the takeover
accepted applies it again, like the Steam autostart takeover.

## A device package on an install that has no room for it

The Plugins folder is one an administrator can copy a package file into, so a device package can
arrive on a Minimal or Desktop install long after setup ran. That combination is otherwise silent:
the package loads, controller management reports itself unavailable, and nothing says why.

`Core\DevicePrerequisites` answers it by looking at the machine rather than at the package, because
a device manifest declares no prerequisites: is a device package in the Plugins folder, is Device
Integration on, is `libviiper.dll` beside WSGM, does the HidHide control device answer. When
something is missing, the overlay's Device page carries a banner naming it: the overlay because it
is the surface a person opens, and the Device page because that is where someone whose device is not
working goes.

The banner offers **Enable Device Integration** and nothing else, and the split is not cosmetic.
Device Integration is WSGM's own setting. The virtual controller needs a kernel driver, and INV-020
forbids the runtime from installing one whatever its provenance: the USB/IP install restarts every
USB 3.0 hub, which drops the built-in controller, the touch digitiser and the keyboard. Underneath a
running Game Mode that leaves a person with no input and no way back, so it happens only while setup
is on screen. For that half the banner says to re-run setup and warns that a reboot follows.

The USB/IP driver itself cannot be upgraded in a boot where anything has attached to it, so its
install and upgrade take two setup runs and a restart; the mechanism and the evidence are in
[device integration](device-integration.md#the-usbip-driver-is-replaced-on-a-boot-of-its-own).

## Order on install and update

1. Record whether the shell is running (mutex `Local\WSGM.Shell`), so WSGM can be restarted in the
   same mode afterwards. The temporary stopped state is never classified as the previous mode.
2. Stop the logon service. A live watchdog would see the stopped WSGM and start Explorer mid-update,
   flipping the restart into desktop mode. Stopping it also frees the service binary. When the
   service state cannot be read, setup stops without changing anything.
3. Signal `Local\WSGM.ExitForUpdate`. One SetEvent releases every WSGM instance, elevated ones
   included. WSGM asks Steam and the launch wrappers to exit under a bounded 10 s pre-stop, then
   runs its own 10 s cleanup, because the mapped Steam Input payload must be replaceable. Setup
   waits for both plus handoff margin (44 half-second polls) before force-stop. A failed Steam
   pre-stop still starts WSGM cleanup.
4. Force-stop fallback: `taskkill` only primary `WSGM.exe` images in setup's own session.
5. Retire the shell anchor. It gets its owner-loss recovery window and is ended only after it
   publishes `Local\WSGM.ShellAnchor.RecoverySettled`, through the same current-session filter.
   Without the acknowledgement it stays alive as the only remaining desktop-recovery owner; the
   `App` swap then fails on its locked image and setup rolls back.
6. Close Steam in every mode, so WSGM starts it with its own settings and an uninstall can remove
   the Steam Input helper from Steam's folder. WSGM's pre-stop only covers an update while WSGM
   runs; whatever is left gets the same graceful `steam://exit` from setup and up to 60 s. Steam is
   never terminated. A fresh install continues when Steam stays open; with WSGM installed, setup
   refuses (step 7).
7. Refuse replacement while Steam or a launch wrapper (`WSGM.Launch`, `WSGM.PackagedLaunch`, plus
   the retired `WSGM.Deelevate` and `steam-input-lease` names) remains in the session. Setup never
   terminates either tree.
8. Reserve `Global\WSGM.DeviceOwner`, so no WSGM or Device Lab runs plugin code during the change.
   Setup waits up to 30 s for a process that just exited, or the old uninstaller's temporary copy,
   to let go of it.
9. Extract the new application to `App.staging`, move `App` to `App.previous` and the new one into
   place, copy the setup and its packages to `Setup`, and replace the installed plugins with the
   chosen ones.
10. `WSGM.exe --setup --answers=<file>` (per-user files, migrate off any legacy shell registration,
    the Xbox-FSE guard, the boot manifest, the answers), then `WSGM.LogonService.exe --install`
    (create-or-reconfigure, failure actions, start). A controller package adds the USB/IP version
    check and HidHide step. The USB/IP check can arrange the separate restart run described below;
    it never replaces the driver in this run. Selected RTSS installation, the bundled WebView2
    runtime, shortcuts and the Installed apps entry follow.
11. After successful interactive completion, start WSGM in its previous mode (`--shell`, or
    Settings), or the session on a fresh install. A quiet update or repair restarts the previous
    runtime; a quiet fresh install does not start it. No path starts WSGM while a driver update is
    pending.

Setup holds one machine-wide owner before staging or choosing packages. A durable
`%PROGRAMDATA%\WSGM\setup-transaction.json` retains the previous application, plugin set, setup
executable, package cache, bundle metadata and registered version until file installation commits. A
fatal failure restores that set before restarting the previous runtime. Recovery that cannot finish
retains its journal and backups, and does not start a mismatched runtime. Resident-shell and sign-in
startup refuse an incomplete transaction; Settings and the early desktop escape remain available.

A later setup first stops the relevant owners and recovers the transaction before detecting
installed packages or preparing answers. Committed or fully restored journals require only backup
cleanup. These are file and registration transactions: user answers, Windows policy, driver
installers and other external writes are not represented as reversible file operations. A child
installer that exceeds its expected duration remains owned and awaited before setup continues.

A WSGM 1.0 installation already removed by its uninstaller cannot be restarted. Setup reports that
case. Updates preserve an explicitly disabled device integration even when its package remains
installed; only a fresh install or explicit package choice supplies a new activation preference.

## Updates

While the session runs, `Shell\UpdateMonitor` asks GitHub for the latest WSGM release once a day
(`Core\UpdateChecker`), two minutes after start at the earliest, and only when **Check for updates**
is on in Settings > System. The latest-release endpoint never returns a prerelease, and a failed or
offline check changes nothing. The result is kept in `%LOCALAPPDATA%\WSGM\update.json`, which
Settings reads; nothing is downloaded until the user asks.

Before offering a release, the check reads its `bundle.json`. An installed community plugin the new
bundle does not carry, because its pinned commit did not build against the new SDK, is named with
its developer contact, and staying on the current version is recommended. It is a warning, not a
block: after the update the new WSGM refuses that file for its `wsgmVersion`.

**Update** asks for confirmation, because the setup closes Steam and WSGM. It then downloads the
setup to `%ProgramData%\WSGM\Updates`, compares it with the release's `.sha256` file, deletes it on
a mismatch, and runs `WSGM.Setup.exe /quiet /update`. The quiet update keeps the user's answers and
device plugin, and restarts WSGM in the mode it was running in.

A quiet update that rolls back says so. Setup shows an error box naming the failed step, writes the
same text to `%ProgramData%\WSGM\update-failed.txt`, and removes that file after the next successful
update. WSGM logs it at shell start and puts it in front of the update status in Settings. The
application folder swap is tried for ten seconds before it counts as failed. Until then a
rolled-back update restarted the old WSGM with nothing to say it had failed, and a tester ran 2.0.1
through two in-app updates (2026-09-28).

## Uninstall

`WSGM.Setup.exe /uninstall` runs from `%ProgramFiles%\WSGM\Setup`, which the Installed apps entry
points at. `Local\WSGM.ExitForUninstall` selects a fixed 20 s WSGM cleanup and does not stop Steam.
Setup then closes Steam itself (step 6 above), because the Steam Input helper cannot be removed
while Steam has it loaded. Then, in order and before any file is deleted: the Steam Input shim
removal, Steam's guide chord template, `--restore-steam-content` (WSGM's boot-movie override leaves
Steam's folder and the movie it set aside returns, and the `themes_custom` link to WSGM's themes
goes; it exits 2 when Steam's own Startup Movie choice is still set aside, and setup then asks the
user to choose it again in Steam), the service `--uninstall` (stop and delete), `--unregister-shell`
(a no-op on service installs, kept as the legacy restore), and `--uninstall-restore`. That last step
first shows every device WSGM hid with HidHide again and takes WSGM's own executable off HidHide's
allowlist and turns the cloak off (`HidHideOwnedDeltaManager.CleanupForUninstallAsync`), whether or
not HidHide itself is removed afterwards. It exits 3 when HidHide did not read back clean, keeps the
ownership ledger, and never retries; setup then names the still-hidden device paths. If the
installed WSGM executable is missing, restoration fails and setup retains the ledger even when the
user chooses to delete settings and data. An unreadable ledger is also retained. Setup's summary
starts WSGM only after a successful install, update or repair; failed runs and uninstall summaries
only close setup.

The uninstall options: **Keep my settings and data** (on by default) keeps `%LOCALAPPDATA%\WSGM` and
the logs; **Custom** lists USB/IP and HidHide when setup installed them, each deselectable so it
stays for another application. A driver that was present before WSGM is never offered.
`%ProgramFiles%\WSGM` is always deleted; the running setup's own folder goes last, through a
detached PowerShell that waits for setup's process to exit, or at the next restart. A file still in
use is scheduled for deletion at the next restart, with its folders after it, deepest first; a path
Windows would neither delete nor schedule fails the step and is named in its note.

## The exit events are a cross-version contract

A newer setup must still release an older running build. The event names, their access grant (user
SID plus Administrators `EVENT_MODIFY_STATE | SYNCHRONIZE`, `0x00100002`), the medium mandatory
label and the startup reset therefore stay compatible (`Core\UpdateExitWatcher.cs`). The unelevated
Settings instance needs the same grant to wait and reset, so narrowing it breaks ordinary update
shutdown.

Session end is a separate path. The resident shell holds a shared `WTSRegisterSessionNotification`
lease, and `WTS_SESSION_LOGOFF` requests the five-second session-end shutdown before Avalonia exits.
Display-mute owns its own lease for unlock recovery, so toggling that feature cannot deregister the
shell's logoff signal.

## The USB/IP driver gets a separate boot

Normal install, update and repair call `Install-UsbipDriver.ps1 -CheckOnly`, including when USB/IP
is already present. The script compares its installed version with the pinned payload; presence
alone is insufficient. A missing or outdated driver makes setup disable the sign-in service and
schedule its installed executable through RunOnce with `/finishdrivers`. The application and package
installation finishes, but WSGM is not started. The interactive result is the Restart page with
**Restart now**; if automatic resumption could not be scheduled, the page asks the user to run setup
again after restarting. A manual completion run must include `/finishdrivers`, because a normal run
still performs only the version check.

`/finishdrivers` is the only mode that installs or upgrades USB/IP. Its plan contains the driver
step and `WSGM.LogonService.exe --install` to turn sign-in startup back on. It does not replay the
profile, replace packages or repeat the application installation. The mode is the authority, without
a marker file or an inferred service-start-type check. This avoids replacing a driver to which WSGM
already attached a controller in the current boot; the failure evidence is in
[device integration](device-integration.md#the-usbip-driver-is-replaced-on-a-boot-of-its-own).

A quiet run never restarts Windows itself. A pending driver-update boot returns 6, which the caller
must handle. The actual driver install can also report a further reboot requirement; a missing
status file reports failure and conservatively sets that requirement. An application-only update
with an up-to-date controller driver does not acquire a restart requirement merely for updating
WSGM.

## Media preview runtime

The setup carries the x64 WebView2 Evergreen standalone runtime in `MediaRuntime`, acquired at build
time from the exact URL and SHA-256 in `eng/webview2-runtime.lock.json`. Staging also validates
Microsoft's Authenticode signature. Setup reads machine/user runtime registrations, skips a runtime
already present and checksum-verifies the extracted installer before running it silently. It does
not download this component. A failure is reported without disabling unrelated WSGM functions;
preview controls explain runtime/playback failures. Uninstall leaves this shared Microsoft runtime
installed because other applications may use it. The app carries the loader/Core SDK and notices,
without introducing WPF or WinForms UI dependencies.

## Command-line and result contract

[`SetupOptions`](../src/WSGM.Setup/SetupOptions.cs) parses the setup switches. With no mode switch,
setup detects a fresh installation, an older version to update, the same version to repair/remove,
or a newer version that it refuses to replace. Version comparison includes the fourth assembly
version component, the build revision.
[`SetupUserIdentity`](../src/WSGM.Install/SetupUserIdentity.cs) refuses a different administrator
account before per-user configuration or recovery state is changed; setup must run as the account
that uses WSGM.

| Option                             | Meaning                                                                                            |
| ---------------------------------- | -------------------------------------------------------------------------------------------------- |
| `/quiet` or `/silent`              | Run the same engine without the page flow, using current answers or explicit input                 |
| `/update`, `/repair`, `/uninstall` | Select the maintenance workflow; uninstall alone accepts `/removedata` and `/keepcomponents`       |
| `/finishdrivers`                   | Complete the scheduled driver-install boot and re-enable the sign-in service                       |
| `/answers=<file>`                  | Supply setup answers instead of the exported/default answers in a quiet install                    |
| `/plugin=<id>` or `/plugin=none`   | Select a bundled device package or no device package in a quiet install                            |
| `/removedata`                      | Uninstall settings/data as well, except recovery ledgers that must remain                          |
| `/keepcomponents`                  | Leave WSGM-installed USB/IP and HidHide installed on uninstall                                     |
| `/payload=<dir>`                   | Use an expanded payload directory for a development setup executable built without an embedded ZIP |

Answers and plugin overrides are consumed by `QuietSetup`; the interactive page flow obtains its
choices from the pages. Unknown switches are rejected. These commands install, remove or recover
live machine state and are not documentation/build validation commands.

| Quiet result | Meaning                                                                        |
| ------------ | ------------------------------------------------------------------------------ |
| `0`          | The plan completed successfully                                                |
| `1`          | Setup failed, including another setup already owning the machine               |
| `2`          | A newer WSGM version is installed                                              |
| `3`          | Uninstall still reports hidden controller devices                              |
| `4`          | Steam is missing                                                               |
| `5`          | No embedded or supplied setup payload is available                             |
| `6`          | WSGM autostart is disabled and a restart is needed to finish the driver update |
| `64`         | Command-line parsing failed before the plan started                            |

The detailed result remains in `setup.log`: optional-component failures can be reported by their
step without undoing the successful application transaction. A quiet `/update` failure also writes
`update-failed.txt` and shows the error described above.

## Source ownership

| Source                                                                                                                                                                                                                                     | Responsibility                                                                                                              |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------- |
| [`Program.cs`](../src/WSGM.Setup/Program.cs), [`SetupOptions.cs`](../src/WSGM.Setup/SetupOptions.cs), [`QuietSetup.cs`](../src/WSGM.Setup/QuietSetup.cs)                                                                                   | Command admission, `Global\WSGM.SetupOwner`, UI/quiet dispatch, kept choices and process results                            |
| [`UI/SetupViewModel.cs`](../src/WSGM.Setup/UI/SetupViewModel.cs), [`UI/Pages.cs`](../src/WSGM.Setup/UI/Pages.cs), [`UI/ProfilePage.cs`](../src/WSGM.Setup/UI/ProfilePage.cs)                                                               | Flow, page-specific close policy, choices, feature descriptions and separate Customize step                                 |
| [`SetupPayload.cs`](../src/WSGM.Setup/SetupPayload.cs)                                                                                                                                                                                     | Embedded ZIP or expanded development payload, bundle loading and staged extraction                                          |
| [`Engine/SetupEngine.cs`](../src/WSGM.Setup/Engine/SetupEngine.cs)                                                                                                                                                                         | Detect, prepare answers, build and execute install/uninstall/driver plans, rollback and restart admission                   |
| [`Engine/SetupFileTransaction.cs`](../src/WSGM.Setup/Engine/SetupFileTransaction.cs)                                                                                                                                                       | Durable file and registration rollback journal, backup retention and interrupted-setup recovery                             |
| [`Engine/RuntimeShutdown.cs`](../src/WSGM.Setup/Engine/RuntimeShutdown.cs) and [`Engine/WindowsSetup.cs`](../src/WSGM.Setup/Engine/WindowsSetup.cs)                                                                                        | Service state, exit-event handoff, current-session process checks, anchor retirement, deletion and deferred cleanup         |
| [`Engine/Registration.cs`](../src/WSGM.Setup/Engine/Registration.cs)                                                                                                                                                                       | Installed version, legacy uninstaller, shortcuts, Installed apps registration and driver-resume RunOnce                     |
| [`Engine/RtssInstaller.cs`](../src/WSGM.Setup/Engine/RtssInstaller.cs), [`Engine/WebViewRuntimeInstaller.cs`](../src/WSGM.Setup/Engine/WebViewRuntimeInstaller.cs), [`Install-UsbipDriver.ps1`](../src/WSGM.Setup/Install-UsbipDriver.ps1) | Component-specific presence/version checks, pinned payload validation and installer execution                               |
| [`WSGM.Install`](../src/WSGM.Install)                                                                                                                                                                                                      | Shared bundle schema, machine/display inventory, ranked offers, component roles, account identity and update-failure record |
| [`Shared/Install/InstallLayout.cs`](../src/Shared/Install/InstallLayout.cs)                                                                                                                                                                | Product roots and pending-transaction admission shared with the logon service                                               |
| [`Core/SetupAnswers.cs`](../src/WSGM/Core/SetupAnswers.cs), [`Core/Installer.cs`](../src/WSGM/Core/Installer.cs) and [`Program.cs`](../src/WSGM/Program.cs)                                                                                | Export/apply user choices and runtime-owned restoration one-shots                                                           |
| [`build.ps1`](../build.ps1) and [`eng/build-bundle.ps1`](../eng/build-bundle.ps1)                                                                                                                                                          | Explicit payload allowlist, source-built native components, curated packages and final setup executable                     |

See [development](development.md) for building the payload and [logging](logging.md) for the
distinction between application, service, launcher and setup logs.
