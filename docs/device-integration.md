# Device integration

Device Integration is an optional, process-long WSGM subsystem that hosts one device plugin
in-process. It is independent from Steam and from Desktop/Game Mode transitions: turning it off
leaves the shell, overlay, Steam Input lease, storage, artwork, launch features, RTSS and core
recovery usable. This document records the decisions behind the runtime and the device findings that
produced them. It does not describe the mechanism step by step.

The resident common PluginHost admits the Device compatibility adapter. DeviceCoordinator owns
machine policy and ordered controller cleanup; hardware behavior stays in DevicePluginRuntime and
the package. Common admission and lifecycle rules are in
[common plugin contracts](plugin-system.md).

Related:

- [device-plugin-system.md](device-plugin-system.md): how each mechanism works, with its budgets,
  boundaries and log lines.
- `src\WSGM.Device.Sdk\docs\reference.md`: the contract a plugin links against.
- [device-plugin-authoring.md](device-plugin-authoring.md): the author workflow and the device
  projects in this repository.

## One plugin slot

Exactly one installed package may exist. Normal startup counts package roots before anything else
runs: manifest validation, device matching, elevation, Explorer exit, Avalonia, plugin loading,
HidHide and virtual-controller creation all come later. Zero packages leaves Device Integration
unavailable. One package is validated and asked to detect the machine; a malformed or nonmatching
package faults only Device Integration and reports the exact package error. Two or more roots refuse
normal UI and shell startup before any device code runs, listing every package name and absolute
path. Recovery, setup, update, uninstall, `--restore-shell` and plugin-removal maintenance bypass
the refusal without starting device code; `--overlay-test` stays simulated.

WSGM never ranks, selects, disables or prefers one package over another. A package is
administrator-installed hardware code running with WSGM's authority. There are no trust tiers,
publisher grants, signer rotation or revocation, quarantine catalog or de-elevated plugin class to
rank with, so ambiguity is refused rather than resolved.

Packages are `.wsgmpkg` files in the administrator-protected `%ProgramFiles%\WSGM\Plugins` folder,
and WSGM loads them straight from the file. A developer package goes to the same folder: there is no
second location, and WSGM never loads plugin code from a user-writable discovery root. A package is
replaced only while WSGM is closed, because a loaded file is held open
([device plugin system](device-plugin-system.md) §3–§7).

The manifest carries id, name, version, exact API version and entry point, plus two lists that let
setup decide without loading code: the `hardware` rules the package is for and the capability roles
it may publish. The runtime refuses any descriptor whose role is not declared, and the plugin's own
detection confirms the hardware rules. Dependencies and policy stay in plugin code.

## Runtime topology and the in-process tradeoff

`ShellSession` creates at most one `DeviceCoordinator` per interactive session. The coordinator
reserves the machine-wide `Global\WSGM.DeviceOwner` marker for the process lifetime, so no other
session, setup or attended Device Lab run can start a second hardware cycle. It loads the sole
package's entry type into one collectible assembly-load context inside WSGM, and that runtime stays
alive across Steam restarts, games and desktop/game transitions. Lifecycle calls and publications
are direct managed calls.

Package bytes cannot change under a loaded plugin: the file is held open read-only while its code
may run, and setup replaces it only after stopping WSGM. When setup or uninstall refuses before
touching files, it restores the initially observed shell or settings mode. It restarts the logon
service only when that service was initially present and running, so startup catch-up cannot launch
a second boot process. The restored process opens the installer's unowned marker and keeps that
second handle for its lifetime, so the session survives without letting setup and a new hardware
cycle overlap.

**The collectible load context isolates dependency resolution; it is not crash containment.** It
permits a clean unload after verified cleanup, but a process-fatal managed or native plugin failure
terminates WSGM with the plugin. The remaining recovery boundary is WSGM's own session recovery plus
the plugin's bounded next-start recovery record. This is the maintenance-cost tradeoff of the
in-process design, not a claim of equivalent isolation.

Plugins publish only the public semantic SDK. WMI, HID, sensor, lighting, firmware, controller and
recovery implementation stays inside the plugin. A plugin cannot supply XAML, JavaScript, URLs,
Steam selectors, shell or file operations, or a raw hardware broker. The SDK (`WSGM.Device.Sdk`,
MIT, maintained under `src\WSGM.Device.Sdk`; `AGENTS.md` explains the licence) deliberately holds no
implementation modules, generic resource leases, WSGM UI policy, source-arbitration projections,
evidence ids or locks, source generators, Steam selectors or CDP patches. Add an abstraction to it
only when the Claw plugin and a materially different plugin both need it.

Glyph artwork and control maps are static plugin data. WSGM validates them and owns every Avalonia
and Steam adaptation. A missing, ambiguous or mismatched profile leaves Valve's glyphs and WSGM's
generic presentation in place.

## Host-first dependency resolution

### A second WinRT.Runtime in the process breaks whichever side initializes second

Any `-windows10.0.x` plugin build copies `WinRT.Runtime.dll` and `Microsoft.Windows.SDK.NET.dll`
beside the plugin, and package authors cannot be expected to trim them. CsWinRT registers a
process-global `ComWrappers` instance when it first runs. A second copy loaded into the plugin
context makes whichever side initializes second fail that registration for the rest of the process.
On the Claw the plugin touched WinRT first, and WSGM's own Wi-Fi and Bluetooth queries died (Claw,
2026-09-01).

`PluginLoadContext.Load` therefore pins the SDK assembly and the WinRT pair to the host's loaded
copies by name, whatever version the package carries. The host's SDK is the type-identity boundary,
and the manifest `apiVersion` is the compatibility gate, not the assembly version. Every other
dependency is asked of the default context first, and the package copy is used only for assemblies
the host does not have or cannot satisfy by version. That duplicate is logged once, because it is
the case that can bite later.

This is the parent-first rule plugin hosts converge on (`PluginLoader.PreferSharedTypes`, Java class
loading). Sharing what the host already owns costs nothing the isolation was buying, and a duplicate
of anything with process-wide state is a fault no later cleanup can undo.

## Lifecycle and recovery

### A modern standby wake must not quiesce the device

A handheld that sleeps does not go to S3. It enters S0 idle, the Desktop Activity Moderator freezes
WSGM's threads for the whole idle period, and Windows hibernates from there. On the way back the
image is resumed **into** S0 idle and leaves it again about a second later. Windows logs
Kernel-Power 506 and 507 one second apart on the wake and delivers a resume, a suspend and a second
resume to the process within a few hundred milliseconds.

Nothing in `PBT_APMSUSPEND` says which standby window it belongs to. Acting on the one in the middle
ran the Claw's controller make-safe on a machine that was already awake: the virtual pad and the
HidHide entry were removed, the suspend failed on a deadline that had expired during the
hibernation, and the resume that followed restarted the whole cycle. Steam did not open the
replacement pad for over three minutes. That happened on all six wakes recorded across 2026-09-25
and 26.

`ShellSession` therefore drops a system suspend that arrives within two seconds of a system resume.
A wake of this shape is a no-op for the device cycle: the plugin, the virtual pad and HidHide stay
exactly as the hibernation image restored them. A suspend that follows ordinary use still quiesces
the cycle. If Windows ever does re-sleep within that window, the cost is a device left running
through a short idle period, not a device left unsafe.

WSGM also treats `PBT_APMRESUMECRITICAL` as a resume. Windows sends it instead of
`PBT_APMRESUMESUSPEND` to a process that never received the suspend, which on this hardware is the
ordinary way a hibernate ends rather than an error.

### The USB/IP driver is replaced on a boot of its own

usbip-win2 cannot be upgraded once anything has attached to it. Installing it restarts every USB 3.0
hub, and the driver's teardown blocks behind the live attachment, so the uninstaller sits at zero
CPU forever and the machine needs a hard reset with the driver half replaced. That is upstream
[#188](https://github.com/vadimgrn/usbip-win2/pull/188), fixed in 0.9.8.1, and it cost the reference
Claw two wedged upgrades on 2026-09-27.

WSGM attaches its virtual pad within seconds of sign-in, so on a machine where WSGM has run there is
no safe moment left in that boot, however early setup starts. The upgrade therefore takes two runs
and one restart:

1. Setup asks the install script what it would do. If an upgrade is due, the step sets the
   `WSGMLogonService` start type to Disabled, writes a `RunOnce` entry that starts setup again with
   `/finishdrivers`, and stops without touching the driver. The run ends on a restart page whose
   only button is Restart now; closing the window instead asks first, because WSGM does not start
   again until that restart happens. A quiet run exits with code 6 and leaves the restart to its
   caller.
2. Nothing starts WSGM at the next sign-in, because the service that would is disabled. This needs
   no cooperation from WSGM and no marker file.
3. `RunOnce` starts setup in `/finishdrivers` mode, which runs exactly two steps: the USB/IP driver,
   then `WSGM.LogonService.exe --install` to put autostart back. That mode is the only one that
   installs the driver; every other run only asks the script and arranges the restart. The mode is
   the whole record: no marker file, and no reading the start type back. The autostart step runs
   whether the driver step succeeded or not.

VIIPER runs `usbip.exe` from PATH to attach the device. usbip-win2 up to 0.9.7.8 added its folder to
the machine PATH; 0.9.8.1's rewritten installer does not, and upgrading removes the old entry, so
the first attach after the update failed with "executable file not found in %PATH%". WSGM now puts
the package's install folder (from its uninstall entry, or `%ProgramFiles%\USBip`) on its own
process PATH before starting the backend when `usbip.exe` is not already reachable.

A fresh install takes the same two runs. Deciding from whether WSGM happened to be running as setup
started was tried first and was wrong: it says nothing about whether a pad was attached earlier in
the same boot, and the install ran and hung anyway (2026-09-27).

### The serialized cycle

The runtime has one serialized lifecycle: detect, start, suspend, resume, stop and diagnostics.
Resume advances a cycle generation before new state or commands are accepted, and stale publications
are refused rather than allowed to cross a resume or controller-reacquisition boundary. Full release
closes command admission, quiesces in-flight commands, performs the controller handoff, stops the
plugin, detaches publications, disposes it and unloads the context only when cleanup was verified. A
command canceled at its caller's deadline keeps its late-completion task, so an eventual hardware
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

RGB restoration cannot save configuration. Automatic desired-value restoration runs whenever the
capability is available and its state is neither stale nor faulted. A value the firmware cannot read
back (`Unknown`) is still restored: readback is never a precondition, because many firmwares cannot
report what they were set to. Fresh lighting readiness admits one restore per saved value and device
cycle. Delayed startup and resume readbacks can admit that first attempt; repeated defaults or
failures cannot repeatedly write firmware. The command result and reconciliation summary retain
failure evidence. These paths have hardware-free regression coverage; the reported RGB reset still
needs a fresh attended startup/resume pass.

Device controls show a pending command's requested value while it runs, then the plugin's observed
value. Saved desired values are only a fallback when no observation exists; they must not hide a
power preset's readback or later firmware changes.

## Controller management

`ControllerManager` is the one WSGM-side owner of the virtual target and its replacement, the haptic
return path, WSGM's own HidHide delta, UI capture, the source WSGM's own surfaces navigate from, and
the make-safe handoff. `DeviceCoordinator` keeps the plugin conversation; the manager orders both
halves. Nothing else creates a target, mutates HidHide or decides where UI input comes from.

### Active emulation raises WSGM's scheduling priority

While controller management is Active, WSGM raises its process priority to High, so normally
scheduled games compete at a lower base priority than input acquisition and virtual report delivery.
Both paths use asynchronous continuations, so changing one thread's priority would not cover the
complete route. The boost also applies to other WSGM work during this interval. Windows Realtime
priority is never requested. See
[Windows scheduling priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities).

The controller manager restores the captured priority when management leaves Active or is disposed.
An existing High or Realtime priority is left alone, and restoration preserves an external change
away from WSGM's High priority. Priority failures are logged at transitions and do not stop input.
Disabled integration and unavailable emulation never acquire the boost. Automated tests cover the
priority lifetime and failure paths; latency under game load still requires attended verification.

### The target is a profile value

`ControllerTarget` resolves like every other profile value: the running game's enabled profile, then
Global, then `SteamDeckComposite`. It is matched with the same rule against the same identity and
executable as the rest of the profile, so the controller target and the performance values cannot
disagree about which application is running ([profiles](profiles.md)).

### Steam wins, the foreground fills, and a tie stays ambiguous

The identity has two sources, and only one is Steam. The foreground application comes from a
WinEvent hook plus a two-second poll, because a hook alone misses focus changes across a lock or an
elevation transition. A UWP window is resolved through `ApplicationFrameWindow` to the process that
owns a child window; otherwise every UWP application would share one profile. The foreground is an
input to the same projection, not a second observer.

Steam wins whenever it names exactly one running application. That identity is the one its launch
went through and the shortcut's executable was resolved from, so alt-tabbing out of a game does not
retarget its profile. The foreground fills only the case where Steam names nothing: the desktop,
another launcher, a title started outside Steam. That is what makes the per-application rows mean
anything outside a Steam game.

The monitor does not break a tie. Two Steam applications leave the state ambiguous, because focus
says which window the user is looking at, not which game they meant to configure. A failed
observation leaves it unavailable rather than claiming an application is running. A foreground
window that is not an application leaves the previous application in force rather than dropping to
the global profile; WSGM's own surfaces count here, since the overlay takes focus at exactly the
moment the user is editing that profile. An unreadable process, which is ordinary for anything
elevated or protected, is treated the same way.

### Only one target exists at a time

A per-application change is one replacement that neutralizes and removes the old target before
creating the new one, so the two are never enumerated together. Any unavailable prerequisite fails
open: a closed release gate, a missing or incompatible backend, unhealthy HidHide, or a target that
does not enumerate. The shell, SDL input and the Steam Input lease continue unchanged, global
HidHide state is untouched, and WSGM's own surfaces stay on the SDL-plus-Steam-lease source.

Capture by a WSGM surface is reference counted and never reaches the target. Controls held when a
surface opens are suppressed until released, and forwarding resumes only on the first sample in
which every control the UI used is up. The press that opened or closed a surface therefore never
arrives in the game as a fresh input.

The controller manager also provides a temporary Steam capture and ownership pause for #65. OEM
Steam Quick Access and Overlay actions invoke this path while managed ownership is active:

- Capture neutralizes the existing target and suppresses both game forwarding and WSGM UI delivery.
  An ownership pause retains that target across physical identity publications; restoration requires
  a newer source generation and verified HidHide activation. Full make-safe clears the pause so a
  later controller start can proceed.
- Replay targets one exact game overlay, or the visible main window when no game overlay is
  registered; multiple game overlays are refused. The same ownership check runs before semantic
  replay, so a request overtaken by teardown is rejected.
- `SteamControllerHandoff` supplies the session-lifetime policy: one admitted replay, observation
  across surface switches and CEF reloads, and one restoration after verified closure or Steam exit.
  Unknown state cannot expire into assumed closure. Unverified writes require recovery instead of
  retry; session shutdown leaves hardware release to full make-safe.
- `DeviceCoordinator` serializes physical release and restoration with its existing lifecycle gate.
  Restoration consumes the saved runtime once and waits for its fresh physical publication; stale
  publications cannot replace the newer controller generation.
- The native adapter grants pass-through after visibility cleanup, then holds a temporary block
  while physical ownership and HidHide return. If suspend, disable or runtime replacement retires
  the saved owner, the interaction stops and drops its native claims without reacquiring hardware.
  Owner changes during restoration are distinguished from unverified writes, so they cannot strand a
  temporary block.
- After surface closure, disconnected physical interfaces delay restoration while Steam access
  remains enabled and the virtual target stays neutral. The host checks the exact instance IDs from
  verified release in the currently configured device tree, without phantom lookup. This is a
  read-only wait; hardware acquisition runs once after presence returns, and owner retirement and
  shutdown cancel the wait. The plugin still revalidates topology and firmware before its write, and
  uncertain writes are not retried.
- Lease-only OEM handoffs use the same native claims without device writes and suspend WSGM SDL
  readers.

End-to-end hardware verification remains deferred to field review. Main-window semantic replay has
live CEF evidence; game-overlay dispatch has deterministic identity/refusal tests only.

### Make-safe removes the target after the physical release and HidHide entries after the target

The handoff is stated in the SDK's `ControllerHandoffStep` vocabulary, not a second WSGM-local one,
so a pasted log settles how far it got. WSGM's half keeps two orderings as explicit guards: the
virtual target may not be removed until the physical release has concluded either way, and WSGM's
HidHide entries may not be removed until the target is gone. Removing either earlier exposes a
device the plugin is still holding, which is the duplicate-input state the single-target rule exists
to prevent. An unverified or failed plugin answer still runs WSGM's removal, and the result records
`ReleasedUnverified` rather than presenting a timeout as a clean release.

### The Deck's digital trigger bits rise at 80 percent travel

Steam reads the Neptune report's two digital trigger bits as "full pull" and the analogue value as
"soft pull"; nothing else produces Full Pull. Raising the bits in the same frame the analogue value
left rest, as Handheld Companion's Deck target does, fired Full Pull before Soft Pull and made every
hip-fire style take the full-pull action. Leaving them clear, as 2.0.1 did, made Full Pull never
fire at all (Xbox Ally X tester, both 2026-09-27). The bits now rise past 80 percent of travel,
which is what HHD's Deck emulation does for every pad without a trigger click, the ROG Ally included
(`trigger_discrete_lvl`). The desktop double-click of 2026-09-02 that was blamed on a mid-travel
threshold came from the 0..65535 trigger scale fixed the same day, not from the threshold. The
DualShock 4 target keeps its digital L2/R2 bits: a real DualShock 4 sets them with the analogue
value. InputPlumber's Deck target uses the same `value > 0.8` rule and the same 32767 full-travel
scale. `tools\DeckSpike` presents a virtual Deck on any PC with usbip-win2 and drives the triggers,
the digital bits and the analogue scale from the keyboard, and `tools\SteamReceiver`, launched from
Steam as a non-Steam game, shows what Steam Input makes of each frame, so such experiments no longer
need a handheld.

Bench result of 2026-09-28, with Steam's default trigger settings (adaptive soft pull at 10000,
analogue range 1000 to 32000, linear curve) and the receiver confirmed in front: a ramp with the bit
rising past 80 percent fired Soft Pull at XInput 94 and Full Pull at XInput 207 on the receiver,
which is the frame in which the bit rose. The bits forced on at rest fired Full Pull with the
triggers at zero. Full travel at 32767 with the bits clear fired Soft Pull only. Full travel written
as 35424 fired nothing and moved nothing, because the field is a signed short and every value above
32767 reads negative; a value "above hardware range" cannot exist on this wire. Full Pull is the bit
and only the bit, and the 80 percent rule fires it where it should. The tester's report of
2026-09-28 without Full Pull came from 2.0.1, which is that run.

### Neptune motion is encoded as raw Deck counts, not normalized axes

Controller management uses VIIPER directly. Its Steam Deck target carries all four rear controls and
the stick-touch fields through usbip-win2's pinned signed driver, and WSGM's encoder supplies the
complete Neptune frame. Motion is converted from the SDK's application axes back to the Deck
report's raw gyro order `X, -Z, Y` at 16 counts per degree per second and 16384 accelerometer counts
per g. Leaving the values as normalized axes was why Steam saw a motion source but no usable gyro
movement. WSGM submits a frame whenever a sample changes, at the sensor's own cadence. VIIPER
completes the endpoint on its 6 ms grid, and its Deck device reports the mean gyro rate since the
previous report, so Steam's per-report integration sees the rotation the samples described whatever
rate the sensor runs at ([performance](perf/README.md#the-gyro-microstutter)). Xbox 360 and
DualShock 4 have their own encoders and are selectable targets. The shell never installs or repairs
a driver at runtime; `external\controller\viiper.md` records the live-device evidence and exact
pins.

The Deck target's return path accepts all three feedback shapes Steam sends: sixteen-bit `0xEB`
rumble, continuous `0xEA` trackpad haptics approximated symmetrically on the physical motors, and
`0x8F` pulses. A pulse carries a bounded, route-generation-checked stop through the serialized
haptic sink, so an old pulse can neither stop a replacement target nor leave the Claw's latched
motors running. An action-only haptic sink has availability but no readback; the overlay treats it
as `Ready` with a `RUN` action and permits its bounded preview. The same holds for every capability:
the router, overlay, native QAM, power presets and AutoTDP command anything the plugin reports
available whose state is neither stale nor faulted, and show a value that was never read back as
"Ready · no readback" instead of disabling it.

The optional installer task owns the initial usbip-win2 and HidHide installation. Its USB/IP helper
is nonfatal but publishes an atomic bounded status under `%ProgramData%\WSGM`, and setup reads that
status instead of treating exit code zero as proof that the signed driver registered. A new
installation requests a reboot; an already-present driver does not; a failed, newer-unreviewed,
missing or malformed result is shown without rolling back WSGM.

### Motion runs only while something reads it

The gyroscope and accelerometer are the highest-rate data WSGM moves. On the Claw the sensor poll
alone cost WSGM 12 % of its idle CPU and the Intel sensor driver host another 5 % of a core with
nothing consuming a sample ([performance](perf/README.md)). The controller manager therefore
computes a motion demand, and the coordinator forwards every change to the plugin through
`IDevicePlugin.SetMotionDemandAsync`. Motion is wanted only while controller management is active on
a target with a motion report (Steam Deck or DualShock 4, never Xbox 360) and, with the "Motion only
on request" setting on (the default), only while a consumer has asked the Steam Deck target for it.
That request is read off the wire, where a consumer cannot hide. Two signals count, both feature
reports VIIPER already hands to the backend's feedback callback:

- **Steam's IMU mode write.** A real Deck controller keeps its IMU off until Steam writes the IMU
  mode setting (report 0x87, setting 0x30) for a layout whose gyro is on, and writes it off again
  for a layout without. Measured on the Claw, 2026-09-26: removing the gyro component from the
  desktop layout released the stream within the second. A settings reset or a new device returns it
  to off.
- **SDL's watchdog heartbeat.** SDL2 and SDL3's Steam Deck driver never ask for the IMU ("on steam
  deck, sensors are enabled by default"), so an SDL application such as Eden or RPCS3 gets no gyro
  from a layout that has none; SteamDeckGyroDSU worked around that by forcing the IMU on itself.
  That driver does write something distinctive while it holds the pad: to keep lizard mode off it
  sends a clear-mappings frame and a single-setting write of right trackpad mode to none every 200
  input reports, and nothing at all when it closes. `MotionDemandTracker` counts one beat as a
  consumer for five seconds, so an SDL reader is present while beats arrive and gone shortly after
  it lets go. WSGM's own SDL ignores the virtual pad (vendor `28de`, product `1205`) rather than
  become a consumer of itself.

The backend raises `IHidBackend.MotionRequested` whenever that combined answer changes. A desktop
emulator gets motion without WSGM knowing it exists, and a game whose layout has no gyro costs
nothing; the earlier "only in game" mode, gated on Steam's running app id, could not tell those
apart. A DualShock 4 target has no such request and always streams. The plugin stops reading the
hardware, not just publishing; the Claw source keeps its measured zero-rate offset across stops so
the first samples after a restart are corrected. A plugin built against an older SDK never sees the
signal and streams as before. The demand is a runtime signal, not a setting the plugin owns, which
is why it is a contract member rather than a declared plugin setting.

### The Steam Deck target loses guide chord edits without help

Steam's guide-chord editor reloads Valve's on-disk template on every edit session for a Steam Deck
controller on Windows, so edits made while the Steam Deck target is active revert within seconds.
While that target is active, the shell runs the guide chord mirror described in
[the Steam Input lease](steam-input.md#guide-button-chord-edits): the "Keep guide button chord
edits" setting in Device Integration, on by default, keeps the template equal to the autosave and
restores Valve's file on reset, target loss, disable and uninstall.

### A target replacement must plug out the usbip client attachment, not only the server device

Attach records the driver-assigned port, and removal issues `IOCTL_PLUGOUT_HARDWARE` for that port
before deleting the server device. Otherwise the closed stream remains as a stale Windows attachment
and the next target is not a true live replacement. This is the focused backport of Handheld
Companion's bundled VIIPER commit `679f7e0`, carried as a downstream commit on the
`KillerPixelCrew/VIIPER` fork, which now tracks `Alia5/VIIPER` upstream.

The managed feedback route closes and the backend target becomes unavailable before plugout. VIIPER
then removes its reverse callback registration, drains callbacks already in flight, and releases its
global C-API mutex across the blocking driver request. That order keeps a final host output packet
from re-entering WSGM during synchronous removal or reaching the physical controller or the
replacement target.

### Claw OEM chord suppression also runs on Desktop

The Claw plugin targets the measured OEM-button orphan `G UP` / `Tab UP` Windows-key bursts while
its OEM service is active, including on the Windows desktop. Its synthetic Win release uses the full
40-byte x64 `INPUT` record with a 32-byte union; the old keyboard-only union made Windows reject the
release and the hook pass the burst through. Normal Win+Tab, modified orphan-up sequences, injected
input, volume keys and unknown sequences remain unfiltered. Hardware-free tests pin the native
layout and sequence behavior; this correction does not claim a new live device pass. The maintainer
reports that Game Mode already works and that switching the same running WSGM session to Desktop
opens Game Bar. That transition leaves the plugin and hook running. The ABI defect is confirmed in
software; the reason the visible symptom differs between modes has not been established by a device
trace.

The follow-up comparison with local HC revision `5c94abca83f8711ff5620906871b31a41c76bf05` found
another difference: Win releases lacked `KEYEVENTF_EXTENDEDKEY`. That flag is now set and covered by
focused tests. At the maintainer's request, WSGM now also intercepts `G DOWN` while Win is held as
HC does, including ordinary keyboard Win+G with Ctrl/Alt/Shift. It consumes repeats and G up after
an accepted synthetic release, even if physical Win up arrives first. Failed releases fail open
without retry on held-key repeats. The measured G/Tab orphan-up path remains. The maintainer's
continued desktop failure reopened the tracker item; the correction still needs an attended check on
the updated installed plugin. No live fix is claimed.

## Authored profiles

A setting is one value WSGM keeps and hands the plugin. A profile is a named shape the user builds
and then applies. They are different records with different homes on purpose, and a curve is refused
as a setting (`PluginSettingDescriptor.TryValidate`) so it cannot acquire two.

Authoring is Settings' job and selection is the overlay's (decision D22b). The selection is the
`FanCurveProfileId` profile value, which names a profile and never holds its contents, so the two
surfaces cannot fight over one record. It resolves like every other profile value
([profiles](profiles.md)).

The chain, and what each link exists to prevent:

| Step    | Owner                                                       | Prevents                                                                                                                   |
| ------- | ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| Author  | `Settings\Pages\PluginSettingsPage`, `Controls\CurveEditor` | A gesture producing a curve the router refuses — every edit goes through `CurveEditing`, so an invalid one cannot be built |
| Store   | `DeviceAuthoredProfile`, `ConfigStore` normalization        | A profile that keys nothing or whose inputs do not ascend surviving to be chosen                                           |
| Select  | `ProfileService`, `ProfileValues.FanCurveProfileId`         | A per-game change silently widening to every game; an override stranded on a stale copy of a curve                         |
| Resolve | `ProfileLayers`                                             | A game and Global disagreeing about which curve is in force                                                                |
| Check   | `DeviceProfileValidation`                                   | A curve authored against bounds the device no longer has                                                                   |
| Apply   | `Shell\DeviceProfileApplier`, `ShellSession`                | The fan curve and the controller target disagreeing about what is running                                                  |

**Selections reference a profile by id, never by copy.** Editing a profile has to change every
application already using it. Copying the curve at selection time would strand every override on the
shape the profile happened to have that day.

**The pre-apply check is not redundant with storage normalization.** Normalization sees only a
profile's internal shape. Profiles are authored with no plugin running (`--settings` starts no
device runtime), so a curve is built against the last known bounds, and the device can be updated,
swapped or downgraded before it is applied. The descriptor is therefore read at apply time and never
cached; a plugin republishes its capabilities across a cycle.

**A bound the descriptor leaves unset is not invented.** An absent minimum means the device declared
no limit there, and supplying one would refuse a curve it would have accepted.

Deleting a profile in Settings clears every layer that selected it, so that layer falls back to the
one below instead of naming nothing. Normalization also drops a reference to a profile that no
longer exists, for a file edited by hand.

Applying counts `AppliedUnverified` as success: many EC writes have no readback, and treating absent
confirmation as failure would report every one of them as broken. A timeout does not count. Whether
it was written is unknown, and claiming success there is the one answer that misleads.

A profile carries a curve or a colour, never both. The capability being authored decides which; a
profile holding an unused half would let a capability change resurrect a value the user set for
something else. Colours are masked to 24 bits on the way in. The picker returns an alpha channel
WSGM has no use for, and a stored value carrying one reads as a wildly different colour when it is
later unpacked as RGB.

The overlay's row states where the current choice comes from, not only its name: "this game's
override" and "from Global" read identically otherwise, and that difference is what the row is
opened mid-game to check. Pressing it saves to the running game's profile while that is on and to
Global otherwise, persisting before applying so a failed save cannot leave the device on a profile
the configuration does not name. Cycling wraps through "none". A selection whose profile was deleted
after the store was loaded reads `MISSING` and stays cyclable, because pressing out of that state is
faster than opening Settings mid-game.

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

Device Lab (`src\WSGM.DeviceLab`) is one optional developer tool with GUI and CLI modes over the
same operations. The main solution builds it and the installer's optional `devicelab` component
publishes it from the same WSGM commit. Change it together with the SDK and plugin consumers in one
commit.

Read-only is the default. One explicit attended action may invoke plugin-owned snapshot, readback or
restore code; it has no `--yes`, bulk, CI, imported-recipe, trial-hash, receipt, evidence-promotion
or remembered-consent route. Every output path is explicit, privacy redaction is mandatory, and the
tool never reads or writes live `%LOCALAPPDATA%\WSGM` data. The attended run reserves the same
`Global\WSGM.DeviceOwner` object as WSGM. If cleanup does not verify, it keeps that object until the
process exits, so a competing WSGM cycle cannot overlap unverified resources
([device plugin system](device-plugin-system.md) §19).

Settings owns startup, integration, controller-ownership, logging and update configuration and the
owner-process requests. Live power, fan, controller, motion, OEM, lighting, glyph, performance and
recovery state belongs on the overlay's Device destination. Overlay, Settings, native QAM and
diagnostics consume the same runtime services rather than parallel policy or projection stacks.
