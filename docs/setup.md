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

The profile page asks every first-run choice once: Full or Minimal, Steam first or Desktop first,
start at sign-in, and Customize for each integration including the Steam autostart takeover. Setup
passes them as setup answers (`--setup --answers=<file>`, `Core\SetupAnswers.cs`). An update or
repair starts from the values WSGM exports (`--export-setup-answers`), so it never silently undoes
Settings. A quiet fresh install never takes over Steam's autostart; a quiet update keeps an accepted
takeover. Quick Setup is retired, and WSGM Settings changes the choices afterwards.

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
undoable, and the write is confirmed by a readback; an unconfirmed one stays pending. Restore only
undoes an entry that still carries WSGM's own value, so a decision the user made afterwards always
wins. HKLM and task changes need elevation and go through the `--disable-steam-autostart` one-shot,
which rescans and takes no name from its command line. A sign-in never prompts: an unelevated
re-check disables user-scope entries and warns about the rest. `--restore-steam-autostart` runs from
the elevated uninstall restore.

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
    (create-or-reconfigure, failure actions, start), then the USB/IP driver and HidHide when the
    plugin needs them, then shortcuts and the Installed apps entry.
11. Start WSGM in its previous mode (`--shell`, or Settings), or the session on a fresh install.

Setup holds one machine-wide owner before staging or choosing packages. A durable
`%PROGRAMDATA%\WSGM\setup-transaction.json` retains the previous application, plugin set, setup
executable, package cache, bundle metadata and registered version until file installation commits. A
fatal failure restores that set before restarting the previous runtime. Recovery that cannot finish
retains its journal and backups, and does not start a mismatched runtime. Normal WSGM and sign-in
startup refuse an incomplete transaction; the early desktop escape remains available.

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
removal, the service `--uninstall` (stop and delete), `--unregister-shell` (a no-op on service
installs, kept as the legacy restore), and `--uninstall-restore`. That last step first shows every
device WSGM hid with HidHide again and takes WSGM's own executable off HidHide's allowlist and turns
the cloak off (`HidHideOwnedDeltaManager.CleanupForUninstallAsync`), whether or not HidHide itself
is removed afterwards. It exits 3 when HidHide did not read back clean, keeps the ownership ledger,
and never retries; setup then names the still-hidden device paths.

The uninstall options: **Keep my settings and data** (on by default) keeps `%LOCALAPPDATA%\WSGM` and
the logs; **Custom** lists USB/IP and HidHide when setup installed them, each deselectable so it
stays for another application. A driver that was present before WSGM is never offered.
`%ProgramFiles%\WSGM` is always deleted; the running setup's own folder goes last, through a
detached PowerShell that waits for setup's process to exit, or at the next restart.

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

## Restart follows the USB/IP driver only

Setup asks for a restart only when it installed the USB/IP driver and the driver either reported a
reboot or reported nothing (stay conservative when the status file is missing). Ordinary updates
never ask. A quiet run never restarts; it only logs the need.
