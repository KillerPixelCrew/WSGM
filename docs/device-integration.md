# Device integration

Device Integration is an optional, process-long WSGM subsystem that hosts one device plugin
in-process. It is independent from Steam and from Desktop/Game Mode transitions: turning it off
leaves the shell, overlay, Steam Input lease, storage, artwork, launch features, RTSS and core
recovery usable. This document records the decisions behind the runtime and the device findings that
produced them. It does not describe the mechanism step by step.

Related:

- `docs\device-plugin-system.md` — how each mechanism works, with its budgets and log lines.
- `external\WSGM.Device.Sdk\docs\reference.md` — the contract a plugin links against.
- `docs\device-plugin-authoring.md` — the author workflow; `docs\device-security.md` — the boundary
  checklist.

## One plugin slot

Exactly one installed package may exist. Normal startup counts package roots before anything else
runs (manifest validation, device matching, elevation, Explorer exit, Avalonia, plugin loading,
HidHide, virtual-controller creation). Zero packages leaves Device Integration unavailable. One
package is validated and asked to detect the machine; a malformed or nonmatching package faults only
Device Integration and reports the exact package error. Two or more roots refuse normal UI and shell
startup before any device code runs, listing every package name and absolute path. Recovery, setup,
update, uninstall, `--restore-shell` and plugin-removal maintenance bypass the refusal without
starting device code; `--overlay-test` stays simulated.

WSGM never ranks, selects, disables or prefers one package over another. A package is
administrator-installed hardware code running with WSGM's authority. There are no trust tiers,
publisher grants, signer rotation or revocation, quarantine catalog or de-elevated plugin class to
rank with, so ambiguity is refused rather than resolved.

The release installer owns one administrator-protected slot under `%ProgramFiles%\WSGM`. A different
package replaces the existing one, and a developer package occupies the same slot; there is no
second location, and WSGM never loads plugin code from a user-writable discovery root. Replacement
goes through the fixed, nondiscoverable `.staging` and `.previous` siblings while the slot gate is
held, so normal discovery never sees a half-published package (`device-plugin-system.md` §6).

The manifest is deliberately minimal: id, name, version, exact API version, entry assembly and entry
type. Hardware identity, dependencies, capabilities and policy are published by plugin code, so the
manifest cannot disagree with what the plugin does.

## Runtime topology and the in-process tradeoff

`ShellSession` creates at most one `DeviceCoordinator` per interactive session. The coordinator
reserves the machine-wide `Global\WSGM.DeviceOwner` marker for the process lifetime, so no other
session, setup, maintenance command or attended Device Lab run can start a second hardware cycle. It
loads the sole package's entry type into one collectible assembly-load context inside WSGM, and that
runtime stays alive across Steam restarts, games and desktop/game transitions. Lifecycle calls and
publications are direct managed calls.

Discovery and elevated install or removal share the crash-recovering `Global\WSGM.DevicePackageSlot`
mutex. Maintenance takes that gate, then the owner marker, and holds both through the filesystem
replacement, so package bytes cannot change under a loaded plugin. When setup or uninstall refuses
before touching files, it restores the initially observed shell or settings mode and restarts the
logon service only when that service was initially present and running, so startup catch-up cannot
launch a second boot process. The restored process opens the installer's unowned marker and keeps
that second handle for its lifetime, so the session survives without letting maintenance and a new
hardware cycle overlap.

**The collectible load context isolates dependency resolution; it is not crash containment.** It
permits a clean unload after verified cleanup, but a process-fatal managed or native plugin failure
terminates WSGM with the plugin. The remaining recovery boundary is WSGM's own session recovery plus
the plugin's bounded next-start recovery record. This is the maintenance-cost tradeoff of the
in-process design, not a claim of equivalent isolation.

Plugins publish only the public semantic SDK. WMI, HID, sensor, lighting, firmware, controller and
recovery implementation stays inside the plugin, and a plugin cannot supply XAML, JavaScript, URLs,
Steam selectors, shell or file operations, or a raw hardware broker. The SDK (`WSGM.Device.Sdk`,
MIT, pinned as `external\WSGM.Device.Sdk`; `AGENTS.md` explains the licence) deliberately holds no
implementation modules, generic resource leases, WSGM UI policy, source-arbitration projections,
evidence ids or locks, source generators, Steam selectors or CDP patches. Add an abstraction to it
only when the Claw plugin and a materially different plugin both need it.

Glyph artwork and control maps are static plugin data. WSGM validates them and owns every Avalonia
and Steam adaptation; a missing, ambiguous or mismatched profile leaves Valve's glyphs and WSGM's
generic presentation in place.

## Host-first dependency resolution

### A second WinRT.Runtime in the process breaks whichever side initializes second

Any `-windows10.0.x` plugin build copies `WinRT.Runtime.dll` and `Microsoft.Windows.SDK.NET.dll`
beside the plugin, and package authors cannot be expected to trim them. CsWinRT registers a
process-global `ComWrappers` instance when it first runs, so a second copy loaded into the plugin
context makes whichever side initializes second fail that registration for the rest of the process.
On the Claw the plugin touched WinRT first, and WSGM's own Wi-Fi and Bluetooth queries were the side
that died (Claw, 2026-09-01).

`PluginLoadContext.Load` therefore pins the SDK assembly and the WinRT pair to the host's loaded
copies by name, whatever version the package carries; the host's SDK is the type-identity boundary,
and the manifest `apiVersion` is the compatibility gate, not the assembly version. Every other
dependency is asked of the default context first, and the package copy is used only for assemblies
the host does not have or cannot satisfy by version. That duplicate is logged once, because it is
the case that can bite later.

This is the parent-first rule plugin hosts converge on (`PluginLoader.PreferSharedTypes`, Java class
loading): sharing what the host already owns costs nothing the isolation was buying, while a
duplicate of anything with process-wide state is a fault no later cleanup can undo.

## Lifecycle and recovery

The runtime has one serialized lifecycle: detect, start, suspend, resume, stop and diagnostics.
Resume advances a cycle generation before new state or commands are accepted, and stale publications
are refused rather than allowed to cross a resume or controller-reacquisition boundary. Full release
closes command admission, quiesces in-flight commands, performs the controller handoff, stops the
plugin, detaches publications, disposes it and unloads the context only when cleanup was verified. A
command canceled at its caller's deadline keeps its late-completion task so an eventual hardware
outcome is observed instead of being misattributed to a later command.

Controller management is an optional child policy, not a plugin-health requirement. A plugin whose
other services are healthy stays `Active` when that child is deliberately off, and its controller
and haptic capabilities publish `ResourceReleased`. A requested acquisition that fails is a degraded
service and is not disguised as the disabled case.

One process shutdown deadline covers normal exit, update, session logoff and uninstall, and is
passed through controller release and plugin restoration; WSGM does not stack a second set of phase
budgets. Startup cancellation after acquisition gets a fresh bounded handoff and stop;
process-lifetime cancellation leaves the runtime for the outer shutdown owner. WSGM-owned target and
HidHide cleanup still runs after an unverified plugin response, and the result is logged as clean,
unverified, timed-out or failed.

A background fault reported by the plugin drives the same make-safe, stop, detach, dispose and
restart path. WSGM retries at most twice, then faults Device Integration for the run with a manual
retry. The fail-open path restores usable input and removes only WSGM-owned state; it never starts,
stops, kills or reconfigures MSI Center, Handheld Companion or any other external manager. Recovery
records only temporary plugin-owned state that was actually changed and could not be restored;
persistent desired RGB and profile state is kept separately. An indeterminate hardware write is
reported to the plugin owner and never blindly retried.

## Controller management

`ControllerManager` is the one WSGM-side owner of the virtual target and its replacement, the haptic
return path, WSGM's own HidHide delta, UI capture, the source WSGM's own surfaces navigate from, and
the make-safe handoff. `DeviceCoordinator` keeps the plugin conversation; the manager orders both
halves. Nothing else creates a target, mutates HidHide or decides where UI input comes from.

### The target is chosen by two layers keyed by the running application

There is one global default plus per-application overrides, both stored directly under device
integration rather than under a per-device profile. The semantic capabilities keep their five
desired-state layers because hardware limits genuinely differ on battery and per profile; a
controller target does not. Overrides are keyed by the canonical running-application identity from
the one `RunningApplicationMonitor`, which also resolves the RTSS profile, so the controller target
and the performance profile can never disagree about which application is running.

### Steam wins, the foreground fills, and a tie stays ambiguous

The identity has two sources, and only one is Steam. The foreground application comes from a
WinEvent hook plus a two-second poll, because a hook alone misses focus changes across a lock or an
elevation transition. A UWP window is resolved through `ApplicationFrameWindow` to the process that
owns a child window, or every UWP application would share one profile. The foreground is an input to
the same projection, not a second observer.

Steam wins whenever it names exactly one running application: that identity is the one its launch
went through and the shortcut's executable was resolved from, so alt-tabbing out of a game does not
retarget its profile. The foreground fills only the case where Steam names nothing (the desktop,
another launcher, a title started outside Steam), which is what makes the per-application rows mean
anything outside a Steam game.

The monitor does not break a tie. Two Steam applications leave the state ambiguous, because focus
says which window the user is looking at, not which game they meant to configure; a failed
observation leaves it unavailable rather than claiming an application is running. A foreground
window that is not an application (WSGM's own surfaces included, since the overlay takes focus at
exactly the moment the user is editing that profile) leaves the previous application in force rather
than dropping to the global profile. An unreadable process, which is ordinary for anything elevated
or protected, is treated the same way.

### Only one target exists at a time

A per-application change is one replacement that neutralizes and removes the old target before
creating the new one, so the two are never enumerated together. Any unavailable prerequisite (closed
release gate, missing or incompatible backend, unhealthy HidHide, a target that does not enumerate)
fails open: the shell, SDL input and the Steam Input lease continue unchanged, global HidHide state
is untouched, and WSGM's own surfaces stay on the SDL-plus-Steam-lease source.

Capture by a WSGM surface is reference counted and never reaches the target. Controls held when a
surface opens are suppressed until released, and forwarding resumes only on the first sample in
which every control the UI used is up, so the press that opened or closed a surface never arrives in
the game as a fresh input.

### Make-safe removes the target after the physical release and HidHide entries after the target

The handoff is stated in the SDK's `ControllerHandoffStep` vocabulary, not a second WSGM-local one,
so a pasted log settles how far it got. WSGM's half keeps two orderings as explicit guards: the
virtual target may not be removed until the physical release has concluded either way, and WSGM's
HidHide entries may not be removed until the target is gone. Removing either earlier exposes a
device the plugin is still holding, which is the duplicate-input state the single-target rule exists
to prevent. An unverified or failed plugin answer still runs WSGM's removal, and the result records
`ReleasedUnverified` rather than presenting a timeout as a clean release.

### Neptune motion is encoded as raw Deck counts, not normalized axes

Controller management uses VIIPER directly. Its Steam Deck target carries all four rear controls and
the stick-touch fields through usbip-win2's pinned signed driver, and WSGM's encoder supplies the
complete Neptune frame. Motion is converted from the SDK's application axes back to the Deck
report's raw gyro order `X, -Z, Y` at 16 counts per degree per second and 16384 accelerometer counts
per g. Leaving the values as normalized axes was why Steam saw a motion source but no usable gyro
movement. Xbox 360 and DualShock 4 have their own encoders and are selectable targets. The shell
never installs or repairs a driver at runtime; `third_party\controller\viiper\README.md` records the
live-device evidence and exact pins.

The Deck target's return path accepts all three feedback shapes Steam sends: sixteen-bit `0xEB`
rumble, continuous `0xEA` trackpad haptics approximated symmetrically on the physical motors, and
`0x8F` pulses. A pulse carries a bounded, route-generation-checked stop through the serialized
haptic sink, so an old pulse can neither stop a replacement target nor leave the Claw's latched
motors running. An action-only haptic sink has availability but no readback; the overlay treats it
as `Ready` with a `RUN` action and permits its bounded preview, rather than labelling the absent
value `Unknown` and disabling the only direct hardware test.

The optional installer task owns the initial usbip-win2 and HidHide installation. Its USB/IP helper
is nonfatal but publishes an atomic bounded status under `%ProgramData%\WSGM`, and setup reads that
status instead of treating exit code zero as proof that the signed driver registered. A new
installation requests a reboot; an already-present driver does not; a failed, newer-unreviewed,
missing or malformed result is shown without rolling back WSGM.

### A target replacement must plug out the usbip client attachment, not only the server device

Attach records the driver-assigned port, and removal issues `IOCTL_PLUGOUT_HARDWARE` for that port
before deleting the server device. Otherwise the closed stream remains as a stale Windows attachment
and the next target is not a true live replacement. This is the focused backport of Handheld
Companion's bundled VIIPER commit `679f7e0`, layered on WSGM's pinned `corando98/VIIPER@024aef3a`
baseline.

The managed feedback route closes and the backend target becomes unavailable before plugout. VIIPER
then removes its reverse callback registration, drains callbacks already in flight, and releases its
global C-API mutex across the blocking driver request. That order keeps a final host output packet
from re-entering WSGM during synchronous removal or reaching the physical controller or the
replacement target.

## Authored profiles

A setting is one value WSGM keeps and hands the plugin. A profile is a named shape the user builds
and then applies. They are different records with different homes on purpose, and a curve is refused
as a setting (`PluginSettingDescriptor.TryValidate`) precisely so it cannot acquire two.

Authoring is Settings' job and selection is the overlay's (decision D22b), which is why
`DeviceProfileSelectionStore` writes only which profile is chosen and never a profile's contents:
the two surfaces cannot fight over one record.

The chain, and what each link exists to prevent:

| Step    | Owner                                                       | Prevents                                                                                                                   |
| ------- | ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| Author  | `Settings\Pages\PluginSettingsPage`, `Controls\CurveEditor` | A gesture producing a curve the router refuses — every edit goes through `CurveEditing`, so an invalid one cannot be built |
| Store   | `DeviceAuthoredProfile`, `ConfigStore` normalization        | A profile that keys nothing or whose inputs do not ascend surviving to be chosen                                           |
| Select  | `DeviceProfileSelection`, `DeviceProfileSelectionStore`     | A per-game change silently widening to every game; an override stranded on a stale copy of a curve                         |
| Resolve | `DeviceProfileSelectionResolver`                            | A deleted profile quietly falling back to someone else's curve                                                             |
| Check   | `DeviceProfileValidation`                                   | A curve authored against bounds the device no longer has                                                                   |
| Apply   | `Shell\DeviceProfileApplier`, `ShellSession`                | The fan curve and the controller target disagreeing about what is running                                                  |

**Selections reference a profile by id, never by copy.** Editing a profile has to change every
application already using it; copying the curve at selection time would strand every override on the
shape the profile happened to have that day.

**The pre-apply check is not redundant with storage normalization.** Normalization sees only a
profile's internal shape. Profiles are authored with no plugin running (`--settings` starts no
device runtime), so a curve is built against the last known bounds, and the device can be updated,
swapped or downgraded before it is applied. The descriptor is therefore read at apply time and never
cached; a plugin republishes its capabilities across a cycle.

**A bound the descriptor leaves unset is not invented.** An absent minimum means the device declared
no limit there, and supplying one would refuse a curve it would have accepted.

Two refusals are deliberately not symmetrical with the rest. A selection naming a deleted profile is
kept by normalization rather than pruned, because the resolver reports it by name and pruning would
turn a diagnosable mistake into an override that vanished without explanation. And it resolves to
nothing rather than falling back to the global choice, because falling back hides that the user's
intent for that application is gone while the fans quietly run another curve.

Applying counts `AppliedUnverified` as success: many EC writes have no readback, and treating absent
confirmation as failure would report every one of them as broken. A timeout does not count; whether
it was written is unknown, and claiming success there is the one answer that misleads.

A profile carries a curve or a colour, never both. The capability being authored decides which, and
a profile holding an unused half would let a capability change resurrect a value the user set for
something else. Colours are masked to 24 bits on the way in: the picker returns an alpha channel
WSGM has no use for, and a stored value carrying one reads as a wildly different colour when it is
later unpacked as RGB.

The overlay's row states the scope of the current choice, not only its name: "Quiet, for this game"
and "Quiet, for everything" read identically otherwise, and that difference is what the row is
opened mid-game to check. Pressing it scopes the change to the running application when there is one
and globally otherwise, persisting before applying so a failed save cannot leave the device on a
profile the configuration does not name. Cycling wraps through "none", and a selection whose profile
was deleted reads `MISSING` and stays cyclable, because pressing out of that state is faster than
opening Settings mid-game.

## HidHide findings

Both findings are from `Shell\HidHideOwnership.cs` (Claw, 2026-08-29).

### Another tool's hide blinds discovery before WSGM's own transaction runs

Handheld Companion had hidden the Claw's pad in both modes with an allowlist naming only itself. SDL
reported no gamepad, the plugin's HID enumeration could not see the pad it had just switched the
device into, and nothing anywhere mentioned HidHide. `EnsureReadableAsync` therefore allowlists WSGM
before the plugin's cycle starts; after discovery has failed it is too late for that cycle.

### HidHide stores application entries as NT device paths

A ledger whose preexisting list already contained `\Device\HarddiskVolume3\…\WSGM.exe` recorded a
delta adding `C:\…\WSGM.exe`. The allowlist grew on every activation, and because cleanup matches
what it wrote, the duplicate in the other notation was left behind on restore. `Contains` and
`NormalizePath` compare both notations for that reason.

## Device Lab and UI ownership

Device Lab (`KillerPixelCrew/WSGM.DeviceLab`, pinned as `external\WSGM.DeviceLab`) is one optional
developer tool with GUI and CLI modes over the same operations. The main solution builds it and the
installer's optional `devicelab` component publishes it from the same commit; change it inside the
submodule, then commit the moved Git link here.

Read-only is the default. One explicit attended action may invoke plugin-owned snapshot, readback or
restore code; it has no `--yes`, bulk, CI, imported-recipe, trial-hash, receipt, evidence-promotion
or remembered-consent route. Every output path is explicit, privacy redaction is mandatory, and the
tool never reads or writes live `%LOCALAPPDATA%\WSGM` data. The attended run reserves the same
`Global\WSGM.DeviceOwner` object as WSGM and, if cleanup does not verify, keeps it until the process
exits so a competing WSGM cycle cannot overlap unverified resources (`device-plugin-system.md` §19).

Settings owns startup, integration, controller-ownership, logging and update configuration and the
owner-process requests. Live power, fan, controller, motion, OEM, lighting, glyph, performance and
recovery state belongs on the overlay's Device destination. Overlay, Settings, native QAM and
diagnostics consume the same runtime services rather than parallel policy or projection stacks.
