# Standing decisions and accepted trade-offs

**Plugin categories share common contracts.** The #49 migration adds an MIT common SDK while
preserving the current Device runtime. Device is an optional singleton specialization; independent
integrations must not require a Device Plugin. Category multiplicity is host policy. Trusted
in-process execution remains the initial model, with no claim that loading or permission
declarations provide a sandbox. The slices and ownership boundary are described in
`plugin-system.md`.

Product-level decisions the other docs assume. Each entry says what was decided and why, and points
to the doc that holds the mechanism. Nothing here is a how-to; when a decision and a mechanism doc
disagree, fix the mechanism doc.

**The product lives in Program Files; state stays per-user.** WSGM setup is one elevated process
because the machine service and the drivers demand it. It installs WSGM, its plugins and a copy of
itself under `%ProgramFiles%\WSGM`, while `%LOCALAPPDATA%\WSGM` and HKCU belong to the elevating
account. This is a single-user-device design. The setup carries every accepted plugin and installs
only the one the hardware matches, so there is no plugin repository and no hot plugin upgrade; the
updater always updates WSGM whole. The update and uninstall ordering, the exit events and the
restart rule live in `docs\boot-and-shell.md`, "Install, update and uninstall".

**The HKCU Winlogon shell replacement is retired.** Running the session without Explorer ever
initializing broke touch features; the Explorer-first service boot is the device-verified fix
(2026-08). 2.0 deleted the registration path, with no install code and no auto mode.
`ShellRegistration.Uninstall`, the snapshot fields in config.json and `--unregister-shell` remain
only as an install's own recovery and the uninstaller's restore. Do not re-register WSGM as the
shell from any new code path.

**Processes WSGM starts inherit its elevation, and that is the point.** An elevated WSGM yields an
elevated Steam, which lets Steam Input reach elevated windows and the Steam Overlay inject into
elevated games; UIPI blocks both otherwise. WSGM's own overlay and edge swipes over elevated windows
ride the same chain. The cost is that an elevated Explorer breaks UWP (touch keyboard, store apps),
which is what de-elevation protects. See `docs\elevation.md`.

**Per-user inputs stay per-user.** The boot manifest, live configuration, HKCU Steam registration
and the install-to-run handoff stay in the user's profile even though WSGM elevates. Keep validation
that improves correctness without changing that model: absolute system-tool paths, no-follow and
no-overwrite file operations, correctly scoped kernel objects, bounded external-data parsers. Do not
add publisher tiers or per-action prompts for inputs the same user already controls.

**The update and uninstall exit events are a cross-version contract.** The names
`Local\WSGM.ExitForUpdate` and `Local\WSGM.ExitForUninstall`, their access grant, label and
stale-signal reset must stay compatible with older running builds. Update asks Steam to exit
normally; a Steam client or launch wrapper that remains is a setup refusal, never a process kill.
Details in `docs\boot-and-shell.md`.

**Windows owns device posture and automatic touch-keyboard policy.** Game and desktop mode never
capture or write `ConvertibleSlateMode` or `TouchKeyboardTapInvoke`.

**Desktop Mode is a complete WSGM session, and Game Mode is launched from it.** Starting at sign-in
and starting in Desktop or Game are separate choices, held as `StartAtSignIn` and `StartMode`. In
Desktop Mode WSGM stays resident with its notification icon, keeps plugins, overlay and performance
services, and starts Steam itself so Steam inherits WSGM's elevation. The overlay or the icon enters
Game Mode. Its launch configuration is WSGM policy in Settings: Default uses the main display;
Custom stores an editable display layout, an optional wait for a display and optional plugin
actions. Leaving restores either the layout captured at entry or a configured Desktop layout. The
reference desktop PC shares an HDMI switch with a TV box, and the TV is invisible to Windows until
that switch selects the PC, so entry must be able to drive external routing and wait without a
deadline. Work is tracked in `_plan\implementation-todo.md`.

**WSGM owns how Steam starts, and setup asks for it.** Windows starting Steam first produces a Steam
without WSGM's integrity, which silently costs Steam Input its reach over elevated windows, so setup
lists the Run entries, Startup shortcuts and scheduled tasks it found and asks the user to let WSGM
take them over; the Full preset pre-ticks that consent line, and a silent install never gives it.
Nothing is deleted. Each entry is disabled the way Task Manager's Startup tab disables it, its
previous state is recorded before the write, every start re-checks for entries that came back, and
uninstall restores exactly what WSGM changed and nothing a user has altered since. This is the
recorded exception to "Settings configures WSGM itself only" (maintainer, 2026-09-12): owning how
Steam starts is WSGM's own behavior, so setup and Settings > System may both do it. Mechanism in
`docs\boot-and-shell.md`, "Steam autostart takeover".

**The volume OSD never interrupts an exclusive game.** The physical volume command is always applied
in game mode. The indicator is non-activating and click-through, and is suppressed only for a
confirmed `QUNS_RUNNING_D3D_FULL_SCREEN` from `SHQueryUserNotificationState`, or an absent or locked
session. `QUNS_BUSY` stays allowed: Steam Big Picture and borderless fullscreen report it.

**One config file, one lock.** Config lives at `%LOCALAPPDATA%\WSGM\config.json`
(`Core\ConfigStore`, System.Text.Json source generation; a new scalar property needs no context
change). The registry snapshots inside it belong to the install lifecycle and feature code never
clobbers them. `ConfigStore.AcquireLock()` is the cross-process scope: the Settings save transaction
holds it across the config write and the splash-asset promotion, while the multi-megabyte image
copies happen outside it (sidecars are per-transaction unique). Nested acquisition on one thread is
free; do not reintroduce stacked 2 s timeouts.

**WSGM is not a controller remapper.** OEM buttons are bound in plugin code, and the closed
`OemAction` vocabulary has no authoring UI on purpose. Every handheld on the market today maps
cleanly onto a Steam Deck controller with no buttons or functions left over, so a rebinding surface
would answer a problem no supported device has while making WSGM responsible for input policy that
belongs to Steam. See `docs\device-plugin-system.md`, "OEM controls".

**The Claw OEM workaround also blocks keyboard Win+G.** After continued desktop Game Bar activation,
the maintainer requested HC's key-down interception on 2026-09-05. The global hook cannot
distinguish the OEM button from ordinary Win+G, so that shortcut, including with modifiers, is
suppressed while the Claw OEM service is active. Normal Win+Tab remains available. Details and the
remaining attended validation are in `docs\device-integration.md`.

**A game profile holds only what was changed for that game.** Everything else is read from Global
when it is resolved and never copied in. Each setting falls back on its own: game, then Global, then
the device keeps what it has. One owner drives Steam's per-game toggle, the overlay and every
consumer, and a setting the running game overrides is marked with a way back to Global. Existing
per-game data was wiped on 2026-09-22 when the model was centralized (the maintainer was the only
2.0 user). Rules in `docs\profiles.md`.

**A device control the user moves is remembered.** A `User` capability write the device accepted is
saved to the profile layer in force: the running game's profile while it is on, Global otherwise.
Mechanism in `docs\device-plugin-system.md`, §11.

**Custom power profiles belong to their power source.** Changing a value included in an applied
device power profile saves the complete observed profile as Custom for AC or battery. Returning to
that source restores those custom values. Per-game changes override only that game; the inactive
source and global defaults remain unchanged. The assignment mechanism and validation boundaries are
in `docs\power-and-display.md`.

**Four display modes became two, and the old Off became Default.** Off, DPI-only and automatic
profiles all collapse into Default, which is the DPI-only posture. Off was never a neutral choice:
it left a handheld running the desktop's scaling inside Big Picture, so DPI-unaware games did not
render 1:1 on the panel, and the setting existed mostly because automatic capture was untrustworthy.
Automatic profiles captured behind the user's back and could learn an exclusive-fullscreen game's
temporary mode as the saved preference. What is left is Default and Custom, and Custom is an
editable saved layout. The 2026-09-13 Display editor provides manual arrangement and per-display
settings, with copying the current desktop optional. See `docs\power-and-display.md`.

**The splash is always shown on Game Mode entry, and its wait has no deadline.** The splash is the
cancel surface, so an entry that has anything to wait for must show it. Its Big Picture timeout
starts when Steam is actually asked, not when the cover goes up: the reference machine waits for a
TV behind an HDMI switch, and a timeout measured from the cover would fire in the middle of exactly
the wait the cover exists for. `Shell\SplashPolicy.cs` is that rule.

**A migrated display layout is not silently rebound.** The retired per-monitor profiles recorded a
GDI source name and a registry device key, neither of which Windows can resolve back to a monitor.
The migration keeps the values and leaves the identity empty, Settings shows those rows as needing
confirmation, and Game Mode entry refuses them. Guessing would move the wrong display; a permanent
compatibility field keyed on the old device key would need live enumeration to mean anything.

**Setup asks which machine, not which components.** The three install modes are named Minimal, MSI
Claw 8 AI+ A2VM and Desktop first, because a person installing WSGM knows what they are putting it
on and does not know what a device-integration runtime or a virtual controller is for. Custom
remains for the people who do. A mode seeds only a machine with no configuration yet: re-running
setup is how repair and upgrade work, and a mode that rewrote the start mode each time would undo
Settings behind the user's back. Details in `docs\boot-and-shell.md`, "Install modes".

**Toolchain pins.** .NET 10 and Avalonia 12.1.2. `LoadingIndicators.Avalonia` is vendored under
`external\LoadingIndicators.Avalonia` and built from source, because its published Avalonia 11
package has precompiled XAML that fails on Avalonia 12; its Unlicense text ships from
`src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.1.0 and an explicit `Avalonia.Controls.ColorPicker`
12.1.2 pin keep the controls on the same Avalonia line. `Avalonia.Labs.Panels` 12.0.2 supplies the
production overlay's FlexPanel layout.

**Overlay glass uses one live compositor backdrop (2026-09-24).** A reusable Avalonia attachment
owns a native companion window behind the Overlay and applies one 8-pixel blur to shared desktop
visuals. Settings > Quick Access adjusts it from 0 to 60 pixels. Its panels contribute translucent
tint, so nested groups do not repeat the blur. The Windows Transparency Effects preference does not
control this path; an unavailable compositor attachment leaves the Overlay opaque and readable. The
backend's private DWM exports are an explicit compatibility risk. The Claw confirmed the separate
sample and integrated Overlay over Steam and a game with no noticeable frame-time change. Details
are in `src\Avalonia.LiveBackdrop\README.md`.

**Desktop recovery is a single transition (2026-09-13).** Normal return and failed Game Mode entry
share ordered, independently guarded cleanup. They restore a responsive Explorer before optional IR
actions and never redirect a failed desktop request back into Game Mode. The shell's window owners
and responsiveness define readiness. After orderly exit has removed both shell surfaces for two
seconds, WSGM may release the retained original process; it never force-closes an active or
replacement shell during takeover. This replaces waiting for every Explorer process to disappear,
which stranded the attended desktop after its taskbar had already exited.

**Launch-parent capture is independent of UI responsiveness (2026-09-13).** The first deployed
redesign refused a valid medium, jobless Explorer after one 100 ms message timeout, before any
Explorer exit request. Capturing the actual matching shell owner now checks identity and token
semantics without a UI-response gate or extra stability delay. Desktop restoration still verifies
responsive windows and stable ownership before reporting success.

**The packaged-game launcher offers two explicit input modes (2026-09-14, shipped 2026-09-22).**
`src/WSGM.PackagedLaunch` delivers [#48](https://github.com/KillerPixelCrew/WSGM/issues/48). Steam
integration for single-player games uses the overlay and input bridge that Moonlighter's attended
trial established. Controller-only mode switches VIIPER to its Xbox 360 target without custom game
injection. Valve controller users in Desktop Mode can therefore lack controller support for
multiplayer games on this route; the maintainer accepts that limitation. Controller-only must not
silently enable the injection route. Neither mode carries a general anti-cheat compatibility
guarantee. The choice is per title on the import page, where a multiplayer title reaches the
injecting route only by an acknowledgement the backend enforces.

**Xbox imports select a launcher by runtime (2026-09-14, shipped 2026-09-22).** The Game Library
delivering [#47](https://github.com/KillerPixelCrew/WSGM/issues/47) identifies UWP/AppContainer
versus packaged Win32/GDK and selects the corresponding route in `src/WSGM.PackagedLaunch`. Both are
packaged; an Xbox source or WindowsApps path does not determine the runtime. Moonlighter required an
AppContainer IPC/input bridge and CoreWindow correction. PowerWash Simulator 2 worked by injecting
Steam into its AAM-created launch helper early and letting Steam follow the game child. This
automatic technical routing remains separate from the user's explicit Steam-integration versus
controller-only choice. Unknown classification must not silently enable injection. See
[Steam launcher handoff](steam-launcher-handoff.md) for the evidence and remaining limits, and
[the packaged-game launcher](packaged-game-launcher.md) for what was built on it.

**Artwork is a WSGM feature, not a plugin (2026-09-22).** The Steam artwork browser was extracted
into a bundled `WSGM.Plugin.Artwork` package and is now folded back into `src/WSGM`. Planning the
Xbox library importer showed the boundary was in the way at every turn: the importer needs the same
providers, the same apply path, the same state store and the same Steam page host. It was also
load-bearing in a way nobody wanted — a second page-owning plugin throws out of
`SteamUiSessionHost`'s constructor, because one patch id belongs to one module, taking every Steam
surface with it. The bundled package had in fact never shipped: `WSGM.csproj` staged it under
`publish\App\Plugins\` and the installer carried no such line, so no installed build ever loaded it.
Page registration is now host-owned and plugins declare routes for the host to merge. The host's own
pages are the artwork browser and the Game Library. The plugin SDK, `src/WSGM.Plugin.Ir` and the
installed third-party path are unchanged.

**Device Lab drives hardware for an attended tester (2026-09-24).** Device Lab used to touch
hardware only through a loaded plugin's `test hardware` action. It is becoming the one tool a
maintainer sends to people with an unknown handheld, and on such a device there is no plugin yet:
finding out what a plugin would have to do is the point. The attended wizard may therefore change
machine state directly: HidHide's allowed-programs list, a PawnIO install, and from later stages the
device's own power, fan, lighting and controller-mode commands. The rules a plugin must follow still
apply: every change is recorded before it is made, read back from a real source, restored, confirmed
by the tester and never retried automatically, and a crashed session is cleaned up on the next
start. The knowledge records the wizard works from are evidence, not a plugin: nothing WSGM runs
reads them, and a device only becomes supported through a plugin built from them. AllyXLab was
retired on 2026-09-25, once the wizard covered it.

**WSGM's own settings can be changed from Steam (2026-09-24).** A WSGM row in Steam's main menu
opens a page with a limited set of WSGM's global settings: which Steam features WSGM injects, how it
starts, Steam Input, and the installed plugins' settings. It is a second place to reach settings
that configure WSGM itself, not a second owner: each change is one field written through the config
store, applied by the shell's reload exactly as a save from WSGM Settings, and an open Settings
window keeps any shared field it did not change itself rather than writing back what it loaded.
Everything on the page is Steam's own UI; an element Steam does not have would go into the toolkit,
reusable and in Steam's exact style. See [WSGM in Steam](wsgm-in-steam.md).

**The Game Library is one feature, and Xbox is its first source (2026-09-23).** Bringing other
launchers' games into Steam is WSGM's own Steam ROM Manager: one backend with a source for each
launcher, a plan, the user's per-title choices kept across scans, a review, an apply, and artwork as
a stage of that pipeline rather than a neighbouring page. It is reached from both of WSGM's
surfaces, the overlay and Steam's Quick Access plugin tab, and both render the same state. Xbox is
the first source, and shipping it needed the packaged-game launcher. The work first shipped as an
Xbox importer beside a separate artwork page, with no overlay surface, because its plan listed edits
rather than describing the finished product; see [the Game Library](game-library.md).

**WSGM writes Steam shortcuts through the running client (2026-09-22).** This reverses the ban in
[Steam launcher handoff](steam-launcher-handoff.md), which was written after CEF shortcut-management
calls destabilized a live Steam session during an attended investigation. The Game Library needs to
create shortcuts, and the alternative — editing `shortcuts.vdf` offline — requires Steam stopped, a
backup, and preservation of every other entry, which is a worse thing to get wrong. The reversal is
narrow and comes with the rules that make it safe: one write at a time with a settle between them,
never in parallel; a new id confirmed by a before/after diff of Steam's own library, which is the
authority over what the call returned; and an unconfirmed or ambiguous result stops the run and is
never retried, because the entry may already exist. If Steam is not running, the importer refuses
rather than falling back. CEF is still barred from diagnosing packaged games.
