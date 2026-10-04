# Standing decisions and accepted trade-offs

Product-level decisions the other docs assume. Each entry says what was decided and why, and points
to the doc that holds the mechanism. Nothing here is a how-to; when a decision and a mechanism doc
disagree, fix the mechanism doc. Dated entries carry the day the maintainer decided them.

## Install and per-user state

**The product lives in Program Files; state stays per-user.** WSGM setup is one elevated process
because the machine service and the drivers demand it. It installs WSGM, its plugins and a copy of
itself under `%ProgramFiles%\WSGM`, while `%LOCALAPPDATA%\WSGM` and HKCU belong to the elevating
account. This is a single-user-device design. The setup carries every accepted plugin and installs
only the one the hardware matches, so there is no plugin repository and no hot plugin upgrade; the
updater always updates WSGM whole. The update and uninstall ordering, the exit events and the
restart rule live in [setup](setup.md).

**Setup asks how the machine is used, not which components to install.** A fresh install chooses
Full or Minimal, Steam first or Desktop first and the sign-in start on one profile page, and
Customize opens the individual switches for the people who want them. Which device plugin and which
drivers go on is decided by matching every bundled plugin's hardware rules against the machine,
because a person installing WSGM knows what they are putting it on and does not know what a
device-integration runtime or a virtual controller is for. The answers seed only a machine with no
configuration yet: re-running setup is how repair and upgrade work, and an update starts from the
values WSGM exports, so it never rewrites a choice Settings changed behind the user's back. Details
in [setup](setup.md#what-gets-installed).

**Per-user inputs stay per-user.** The boot manifest, live configuration, HKCU Steam registration
and the install-to-run handoff stay in the user's profile even though WSGM elevates. Keep validation
that improves correctness without changing that model: absolute system-tool paths, no-follow and
no-overwrite file operations, correctly scoped kernel objects, bounded external-data parsers. Do not
add publisher tiers or per-action prompts for inputs the same user already controls.

**The update and uninstall exit events are a cross-version contract.** The names
`Local\WSGM.ExitForUpdate` and `Local\WSGM.ExitForUninstall`, their access grant, label and
stale-signal reset must stay compatible with older running builds. Update asks Steam to exit
normally; a Steam client or launch wrapper that remains is a setup refusal, never a process kill.
Details in [setup](setup.md#the-exit-events-are-a-cross-version-contract).

**One config file, one lock.** Config lives at `%LOCALAPPDATA%\WSGM\config.json`
(`Core\ConfigStore`, System.Text.Json source generation; a new scalar property needs no context
change). `Program` builds one `UserDataContext` (data root and mutex name) and one `ConfigStore`
over it, and passes them to everything that reads or writes per-user state; nothing derives the root
from the logger or the environment on its own. The registry snapshots inside the config belong to
the install lifecycle and feature code never clobbers them. `ConfigStore.Transaction()` is the
single cross-process writer scope: the Settings save holds it across the config write and the
splash-asset promotion, while the multi-megabyte image copies happen outside it (sidecars are
per-transaction unique). A writer is never nested; a second writer or a read on the writer's thread
is refused rather than stacking 2 s timeouts.

**Config schema.** `AppConfig.SchemaVersion` is 1; a 2.0 file has none and reads as 0. Reading
migrates an older file in memory, rewriting only stored values whose meaning changed (2.0's
editor-made game and desktop layouts go from rotation 1 to 0, "keep the display's rotation"), and
the next strict write persists it. Before that first write the store keeps the old bytes once as
`config.v0.json`, and refuses the write if it cannot. A file from a newer WSGM loads best effort.
Legacy `BoostWatts` moves into the device PL2 entry at runtime, when the device publishes it. A
stored custom tab without an id gets `tab-<index>-<name>` on every read instead of a random id.

**Toolchain pins.** .NET 10 and Avalonia 12.1.2. `LoadingIndicators.Avalonia` is vendored under
`external\LoadingIndicators.Avalonia` and built from source, because its published Avalonia 11
package has precompiled XAML that fails on Avalonia 12; its Unlicense text ships from
`src\WSGM\Licenses\`. `FluentAvaloniaUI` 3.1.0 and an explicit `Avalonia.Controls.ColorPicker`
12.1.2 pin keep the controls on the same Avalonia line. `Avalonia.Labs.Panels` 12.0.2 supplies the
production overlay's FlexPanel layout. `Microsoft.Data.Sqlite` 10.0.12 reads launchers' SQLite
databases for the Game Library; its MIT text and SQLitePCLRaw's Apache 2.0 text ship from
`src\WSGM\Licenses\`.

## Shell, Explorer and elevation

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
which is what de-elevation protects. See [elevation](elevation.md).

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
[setup](setup.md#the-steam-autostart-takeover).

**WSGM keeps the other handheld managers off, and Settings can take over again.** Full mode's
takeover of Handheld Companion and the maker's apps is not a one-time setup step: Handheld
Companion's uninstaller re-enables the maker's services, and a user then meets Armoury Crate's
install dialog on the Armoury Crate button (Ally tester, 2026-09-27). Every shell start re-checks
without prompting, and Settings > System offers "Take over again" beside the Steam autostart button
under the same recorded exception (maintainer, 2026-09-27): being the one manager of the device is
WSGM's own behavior. Mechanism in [setup](setup.md#the-other-handheld-managers).

**The volume OSD never interrupts an exclusive game.** The physical volume command is always applied
in game mode. The indicator is non-activating and click-through, and is suppressed only for a
confirmed `QUNS_RUNNING_D3D_FULL_SCREEN` from `SHQueryUserNotificationState`, or an absent or locked
session. `QUNS_BUSY` stays allowed: Steam Big Picture and borderless fullscreen report it.

**The splash is always shown on Game Mode entry, and its wait has no deadline.** The splash is the
cancel surface, so an entry that has anything to wait for must show it. Its Big Picture timeout
starts when Steam is actually asked, not when the cover goes up: the reference machine waits for a
TV behind an HDMI switch, and a timeout measured from the cover would fire in the middle of exactly
the wait the cover exists for. `Shell\SplashPolicy.cs` is that rule.

**Desktop recovery is a single transition (2026-09-13).** Normal return and failed Game Mode entry
share ordered, independently guarded cleanup. They restore a responsive Explorer before optional IR
actions and never redirect a failed desktop request back into Game Mode. The shell's window owners
and responsiveness define readiness. Explorer is only ever asked to leave, never terminated (see
**Explorer is never killed**). This replaces waiting for every Explorer process to disappear, which
stranded the attended desktop after its taskbar had already exited.

**Explorer is never killed (2026-09-26).** Game Mode entry posts Explorer's orderly exit and, for a
retired process that outlives its shell surfaces, only `WM_CLOSE` to its windows. Killing it makes
Winlogon respawn the shell; the 2026-09-13 release did that on every Xbox Ally X entry and broke the
return to Game Mode. Desktop return waits, bounded, for a retired process still finishing.

**Launch-parent capture is independent of UI responsiveness (2026-09-13).** The first deployed
redesign refused a valid medium, jobless Explorer after one 100 ms message timeout, before any
Explorer exit request. Capturing the actual matching shell owner now checks identity and token
semantics without a UI-response gate or extra stability delay. Desktop restoration still verifies
responsive windows and stable ownership before reporting success.

## Profiles, power and display

**A game profile holds only what was changed for that game.** Everything else is read from Global
when it is resolved and never copied in. Each setting falls back on its own: game, then Global, then
the device keeps what it has. One owner drives Steam's per-game toggle, the overlay and every
consumer, and a setting the running game overrides is marked with a way back to Global. Existing
per-game data was wiped on 2026-09-22 when the model was centralized (the maintainer was the only
2.0 user). Rules in [profiles](profiles.md).

**A device control the user moves is remembered.** A `User` capability write the device accepted is
saved to the profile layer in force: the running game's profile while it is on, Global otherwise.
Mechanism in [device plugin system](device-plugin-system.md), §11.

**A driver that keeps per-application values keeps WSGM's per-game values too (2026-09-29).** GPU
drivers apply their own per-application profiles when a game starts, and some settings take effect
only then or only after a restart. Rewriting such a value on every game switch would fight the
driver and still miss the game's launch. A capability therefore says how its value travels
(`ProfileScope`): switched as before, global only, or native per-application, where WSGM keeps each
game's value in the game's profile as always, writes only the Global value itself and hands every
game's values, with its executables, to the plugin that stores them in the driver. WSGM learns a
Steam game's executable the first time it sees the game run, because a store title names none; the
name never activates a profile. Rules in [profiles](profiles.md).

**Custom power profiles belong to their power source.** Changing a value included in an applied
device power profile saves the complete observed profile as Custom for AC or battery. Returning to
that source restores those custom values. Per-game changes override only that game; the inactive
source and global defaults remain unchanged. The assignment mechanism and validation boundaries are
in [power and display](power-and-display.md).

**CPU boost is HC's control, carried per game (2026-09-26).** An Xbox Ally X tester asked for
Armoury Crate's and Handheld Companion's CPU boost option in the Performance quick settings, since
some games want it on and others off. WSGM ports HC's mechanism unchanged, HC's five modes written
to Windows' processor boost setting on both power sources with the scheme re-activated, and makes it
a profile value like the frame limit rather than a global switch, so the choice follows the game. It
is Windows power policy, so it lives beside the processor core preference on the overlay's
Performance section and Steam's Performance tab and needs no device plugin.

**Four display modes became two, and the old Off became Default.** Off, DPI-only and automatic
profiles all collapse into Default, which is the DPI-only posture. Off was never a neutral choice:
it left a handheld running the desktop's scaling inside Big Picture, so DPI-unaware games did not
render 1:1 on the panel, and the setting existed mostly because automatic capture was untrustworthy.
Automatic profiles captured behind the user's back and could learn an exclusive-fullscreen game's
temporary mode as the saved preference. What is left is Default and Custom, and Custom is an
editable saved layout. The 2026-09-13 Display editor provides manual arrangement and per-display
settings, with copying the current desktop optional. See [power and display](power-and-display.md).

## Steam

**WSGM's own settings can be changed from Steam (2026-09-24).** A WSGM row in Steam's main menu
opens a page with a limited set of WSGM's global settings: which Steam features WSGM injects, how it
starts, Steam Input, and the installed plugins' settings. It is a second place to reach settings
that configure WSGM itself, not a second owner: each change is one field written through the config
store, applied by the shell's reload exactly as a save from WSGM Settings, and an open Settings
window keeps any shared field it did not change itself rather than writing back what it loaded.
Everything on the page is Steam's own UI; an element Steam does not have would go into the toolkit,
reusable and in Steam's exact style. See
[WSGM's settings page in Steam](steam-cef-system.md#wsgms-settings-page-in-steam).

**Themes are CSS Loader's themes, from CSS Loader's store (2026-09-28).** WSGM restyles Big Picture
with CSSLoader-compatible themes rather than a format of its own, and browses and installs them from
DeckThemes, the feed CSS Loader uses, with the same query. A theme written for a Deck works on WSGM
unchanged, profiles and dependencies included, and a themes folder copied over keeps its saved state
because WSGM keeps it in CSS Loader's own file. The one piece taken on with that is the class-name
translation table DeckThemes publishes per Steam build; WSGM fetches it as CSS Loader does and
cannot keep it current itself. The toolkit installs the blocks through Steam's popup manager from
SharedJSContext instead of a debugger session per window, which is the one thing about the mechanism
that is WSGM's. Starring and submissions need an account and are left out.

**Artwork is a WSGM feature, not a plugin (2026-09-22).** The Steam artwork browser was extracted
into a bundled `WSGM.Plugin.Artwork` package and is now folded back into `src/WSGM`. Planning the
Xbox library importer showed the boundary was in the way at every turn: the importer needs the same
providers, the same apply path, the same state store and the same Steam page host. It was also
load-bearing in a way nobody wanted: a second page-owning plugin throws out of
`SteamUiSessionHost`'s constructor, because one patch id belongs to one module, taking every Steam
surface with it. The bundled package had in fact never shipped: `WSGM.csproj` staged it under
`publish\App\Plugins\` and the installer carried no such line, so no installed build ever loaded it.
Page registration is now host-owned and plugins declare routes for the host to merge. The host's own
pages are the artwork browser and the Game Library. The plugin SDK, `src/WSGM.Plugin.Ir` and the
installed third-party path are unchanged.

**The Game Library is one feature, and Xbox is its first source (2026-09-23).** Bringing other
launchers' games into Steam is WSGM's own Steam ROM Manager: one backend with a source for each
launcher, a plan, the user's per-title choices kept across scans, a review, an apply, and artwork as
a stage of that pipeline rather than a neighbouring page. It is reached from both of WSGM's
surfaces, the overlay and Steam's Quick Access plugin tab, and both render the same state. Xbox is
the first source, and shipping it needed the packaged-game launcher. The work first shipped as an
Xbox importer beside a separate artwork page, with no overlay surface, because its plan listed edits
rather than describing the finished product; see [the Game Library](game-library.md).

**The Game Library reads every major launcher, the way Steam ROM Manager does (2026-09-27).** The
maintainer asked for the importer to work like Steam ROM Manager. Sources are detected on their own
and ticked in a sidebar: Xbox, Epic Games, GOG Galaxy, Ubisoft Connect, Battle.net, itch, Amazon
Games, Prism Launcher, ATLauncher and folders of shortcuts. Titles other than Xbox launch by an
exact command, through the launcher or directly, never through Explorer or a PowerShell wrapper.
Artwork is chosen before saving, per title or for every title at once, from the launcher's own
images, SteamGridDB and Screenscraper. A title started through its launcher runs through
`WSGM.PackagedLaunch --follow`, which starts the launcher outside Steam's tree and stays alive while
the game runs, recognised by its install folder or, for Minecraft, its instance folder, so Steam
keeps the title running and its controller layout; it injects nothing. Minecraft is not launched by
a hand-built Java command, which would carry an account token that expires within a day. A
launcher's own SQLite database is read with `Microsoft.Data.Sqlite`, which ships its own SQLite
rather than depending on the copy in Windows. Steam has no file picker, so the toolkit draws one.
ROM folders wait for the emulator installer. See [the Game Library](game-library.md).

**WSGM writes Steam shortcuts through the running client (2026-09-22).** This reverses the rule
recorded after the September 2026 launcher trials, when CEF shortcut-management calls destabilized a
live Steam session during an attended investigation
([evidence](packaged-game-launcher.md#evidence-the-attended-trials-of-september-2026)). The Game
Library needs to create shortcuts, and the alternative, editing `shortcuts.vdf` offline, requires
Steam stopped, a backup, and preservation of every other entry, which is a worse thing to get wrong.
The reversal is narrow and comes with the rules that make it safe: one write at a time with a settle
between them, never in parallel; a new id confirmed by a before/after diff of Steam's own library,
which is the authority over what the call returned; and an unconfirmed or ambiguous result stops the
run and is never retried, because the entry may already exist. If Steam is not running, the importer
refuses rather than falling back. CEF is still barred from diagnosing packaged games.

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
[the packaged-game launcher](packaged-game-launcher.md) for what was built and the evidence it rests
on.

## Devices and plugins

**Plugin categories share common contracts.** The #49 migration adds an MIT common SDK while
preserving the current Device runtime. Device is an optional singleton specialization; independent
integrations must not require a Device Plugin. Category multiplicity is host policy. Trusted
in-process execution remains the initial model, with no claim that loading or permission
declarations provide a sandbox. The slices and ownership boundary are described in
[common plugin contracts](plugin-system.md).

**Graphics drivers get their own plugin category (2026-09-29, #178).** Variable refresh, sharpening,
colour and latency belong to the GPU driver, not to the handheld, and the same driver runs on
machines no device package supports. They live in `wsgm.gpu` packages: common packages, several at
once (an Intel iGPU and an NVIDIA dGPU), beside the single device package and independent of device
integration and of the device owner. A package runs by default where a display adapter matches its
manifest and never elsewhere, so setup offers it by the same match and writes no enable entry; an
explicit disable still wins. Each package publishes Device SDK capabilities through its own router,
never merged with the device's, because the device consumers pick the power limit, a fan or VRR by
role and expect one match. Variable refresh moved from the Claw package to the Intel one and keeps
its typed profile value. Mechanism in
[common plugin contracts](plugin-system.md#graphics-packages-wsgmgpu).

**WSGM is not a controller remapper.** OEM buttons are bound in plugin code, and the closed
`OemAction` vocabulary has no authoring UI on purpose. Every handheld on the market today maps
cleanly onto a Steam Deck controller with no buttons or functions left over, so a rebinding surface
would answer a problem no supported device has while making WSGM responsible for input policy that
belongs to Steam. The one button left over, the Xbox Ally's Armoury Crate beside its Xbox and
Library buttons, is marked as the companion-application button and opens the WSGM overlay by default
(maintainer, 2026-09-26). See [device plugin system](device-plugin-system.md#13-oem-controls).

**Claw chord suppression retains the working state machine (2026-10-01).** The incomplete firmware
sequence cannot be handled by ordinary shortcut remapping. The orphan-only rewrite opened Game Bar
alongside WSGM. The maintainer directed restoration of the implementation before `24368aef` and
provided the working `BlockWinG.zip` PoC. Preserve interception of G down and orphan G up, target
repeat/release state and the synthetic Win release. Details are in
[device integration](device-integration.md#claw-oem-chord-suppression-also-runs-on-desktop).

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

**Handheld Companion is the Ally plugin's primary reference, buttons included (2026-09-25).** The
ROG Ally plugin was first built with HHD as the authority for buttons, because HC's button handling
had been reported buggy. WSGM runs on Windows, and HC 1.3.1.6 is the reference that ships and is
used on these devices under Windows, so its event semantics now lead: 0x93 is a separate Library
control and 0xA7/0xA8 are M2 press and release. HHD stays the cross-check and the source for what HC
does not do. HC's controller tables are sent as HC sends them, even where they differ from HHD's: an
Xbox Ally X refused the HHD-layout set. A Device Lab observation beats both. The per-fact record is
`src/WSGM.Device.Asus.RogAlly/PROVENANCE.md`.

**WSGM owns HidHide's cloak (2026-09-28).** WSGM used to treat the cloak switch as a prerequisite it
only read, and Handheld Companion's uninstaller turns it off. Anyone who removed HC would then run
WSGM with no virtual pad and Steam on the physical controller, and nothing would say so. Controller
management turns the cloak on when it is off, and every exit, handoff, update and uninstall path
turns it off again after WSGM's entries are removed. Sleep and fault recovery keep the pad hidden,
because WSGM takes it again at once, with a 20-second active-time limit if nothing comes back. WSGM
closes, the original controller comes back; nobody is stranded on a hidden pad. Other owners'
entries are still never touched. Evidence is in
[device integration](device-integration.md#wsgm-owns-the-cloak).

## User interface

**Only the boot movie is replaceable (2026-09-28).** Issue #43 asked for a toolkit primitive to play
SteamOS's suspend animation on Windows. The Windows client turned out to honour
`config\uioverrides\movies` through its generic override route, so no primitive is needed and #45 is
file, catalog and choice work. The suspend movies are overridable too, but they play only in Steam's
own suspend flow, and nothing on Windows drives it: the power button delivers one press edge and no
release (#116), so WSGM sleeps Windows directly (#21). #45 therefore offers the boot movie alone,
under the name the bundle asks for on Windows, `bigpicture_startup.webm`, not Animation Changer's
`deck_startup.webm`. The movie is copied, not linked. Content and choice are kept apart so a shuffle
and a return to Steam's own never touch the downloads, and the choice is applied before Steam starts
because the client caches the lookup for the life of the document.

**A Windows power profile is offered only when there is a choice (2026-09-28).** A machine whose
Windows installation has one power plan showed a dropdown with one entry in the overlay's Power page
and on Steam's Performance tab. Both now show the picker only when Windows enumerates more than one
plan: the overlay hides the section and its pin, and the Steam row publishes no options, which is
how a processor with one kind of core already hides its row.

**Overlay glass uses one live compositor backdrop (2026-09-24).** A reusable Avalonia attachment
owns a native companion window behind the Overlay and applies one 8-pixel blur to shared desktop
visuals. Settings > Quick Access adjusts it from 0 to 60 pixels. Its panels contribute translucent
tint, so nested groups do not repeat the blur. The Windows Transparency Effects preference does not
control this path; an unavailable compositor attachment leaves the Overlay opaque and readable. The
backend's private DWM exports are an explicit compatibility risk. The Claw confirmed the separate
sample and integrated Overlay over Steam and a game with no noticeable frame-time change. Details
are in `src\Avalonia.LiveBackdrop\README.md`.

## RTSS lifetime

**RTSS starts with WSGM and stays running (2026-10-03).** Enabled integration probes immediately,
starts only the verified executable when it is absent, and restarts a discovered or started process
after its exit. The session poll remains active without a performance UI. There is no restart
cooldown; one in-flight launch guard includes the 10 s initialization settle. Disabled integration
never launches or writes, and WSGM never kills RTSS. An unresolved game executable holds only the
frame limit; the overlay level applies through the global profile meanwhile. Details are in
[RTSS integration](rtss.md#wsgm-starts-rtss).
