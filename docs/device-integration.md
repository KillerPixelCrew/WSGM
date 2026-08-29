# Device integration

Device Integration is an optional, process-long WSGM subsystem. It is independent from Steam and
Desktop/Game Mode transitions. Turning it off leaves the shell, overlay, Steam Input lease, storage,
artwork, launch features, RTSS, and core recovery usable.

## One protected plugin slot

Normal startup counts package roots before manifest validation, device matching, elevation, Explorer
exit, Avalonia initialization, `ShellSession`, DeviceHost, HidHide, or virtual-controller creation.

- Zero packages starts core WSGM with Device Integration unavailable.
- One package is validated and asked to detect the current machine. A malformed or nonmatching
  package faults only Device Integration and reports the exact package error.
- Two or more package roots refuse normal WSGM UI and shell startup before device code runs. The
  error lists every package name and absolute path. WSGM never ranks, selects, disables, or prefers
  one package.
- Recovery/setup/update/uninstall, `--restore-shell`, and the dedicated plugin-removal maintenance
  path bypass this refusal without starting device code. `--overlay-test` remains simulated.

The release installer owns one administrator-protected slot under `%ProgramFiles%\WSGM`. Installing
a different device package replaces the existing package; a developer package occupies the same
logical slot. Managed maintenance and setup both use the fixed, nondiscoverable `.staging` sibling,
park the old slot at `.previous`, and atomically publish `installed`. They reconcile the older
`.installed.previous` managed-maintenance name and remove abandoned GUID staging siblings while the
package-slot gate is held. A maintenance source that overlaps `installed`, either fixed sibling, the
legacy recovery sibling, or a legacy staging namespace is rejected by lexical path and existing
filesystem identity before reconciliation mutates any of them. A source that traverses a
link/reparse point is rejected at the same pre-reconciliation boundary, including when its leaf is
missing. Existing path components are then held against replacement, and every enumerated file or
directory is opened without following reparse points before its handle is copied or traversed. Slot,
recovery, and current or legacy staging attributes are inspected exactly before cleanup or
replacement; access and I/O failures refuse mutation instead of being treated as absent paths. An
unrelated missing source still reconciles a parked package before failing. WSGM never loads plugin
code from a user-writable discovery root.

The minimal `plugin.wsgm.json` identifies the package ID, name, version, exact API version, entry
assembly, and entry type. Hardware identity, dependencies, capabilities, and operational policy are
published by plugin code instead of duplicated in the manifest. Runtime validation accepts only an
AMD64 managed assembly with a readable CLR header and assembly metadata, and bounds all files plus
directories before sorting or traversing them.

## Runtime topology

`ShellSession` creates at most one `DeviceCoordinator` for the interactive session. The exact
`Global\WSGM.DeviceOwner` marker prevents any session, setup, maintenance command, or attended
Device Lab run from starting a second machine hardware cycle. The runtime then takes a global
DeviceHost process snapshot and admits the coordinator only when that snapshot proves no earlier
host remains alive; a running or unverified host releases the new marker and fails closed. The
admitted runtime validates the sole package, launches one `WSGM.DeviceHost.exe`, and keeps that
cycle across Steam restarts, games, and desktop/game transitions. Runtime discovery plus host
creation and elevated install/removal share the exact crash-recovering
`Global\WSGM.DevicePackageSlot` mutex. A dedicated owner thread keeps its release correct across
asynchronous continuations. Maintenance atomically reserves the hardware marker and rechecks every
DeviceHost only after acquiring that gate, then keeps both reservations through filesystem
replacement. Setup follows the same gate/owner ordering and refuses publication if process
enumeration cannot prove that every DeviceHost exited. Uninstall holds the same objects through
package and DeviceHost deletion.

When setup or uninstall refuses before file mutation, it restores the initially observed
shell/settings mode and restarts the logon service through its installer-tagged start only when that
service was initially present and running, so startup catch-up cannot launch a second boot process.
If the DeviceHost process check was unverified, that one restored shell process does not create a
DeviceCoordinator. The restored shell or settings process instead opens the installer's existing
unowned global marker, acknowledges the second handle, and retains it for the rest of its process
lifetime before setup releases its copy. This preserves the session without letting an orphaned host
overlap another hardware owner.

DeviceHost loads only the package-local entry assembly and dependencies. One ACL-restricted named
pipe carries exact-version lifecycle, capability, command, state, output, and diagnostic messages.
The measured fixed shared-memory ring remains the single high-rate controller/motion path. The host
is assigned to a kill-on-close job so forced WSGM exit cannot orphan plugin ownership.

The installed plugin is explicit administrator-installed hardware code and inherits WSGM's required
authority. The process boundary limits crash and dependency fallout; it is not a malware sandbox.
There are no runtime trust tiers, publisher grants, signer rotation/revocation, package ranking,
quarantine catalog, or de-elevated plugin class.

Plugins publish only the public semantic SDK. WMI, HID, sensors, lighting, firmware, controller, and
recovery implementation stays inside the plugin. A plugin cannot supply XAML, JavaScript, URLs,
Steam selectors, arbitrary shell/file operations, or a raw hardware broker.

## Lifecycle and recovery

The runtime has one lifecycle: detect, start, suspend, resume, stop, and diagnostics. Suspend/lock
quiesces the current plugin; resume re-detects the machine and advances one cycle generation before
new state or commands are accepted.

If the authenticated coordinator pipe closes after plugin startup, DeviceHost invokes the same
bounded plugin stop before unloading it. Forced parent death can still terminate the kill-on-close
job first, so the plugin's small next-start recovery record remains the crash-recovery boundary.
Startup cancellation follows the same ownership rule: caller cancellation gets a fresh bounded
controller handoff and plugin stop before DeviceHost disposal, while process-lifetime cancellation
preserves the client for the outer shutdown owner to stop under its application deadline.

One process shutdown deadline covers normal exit, update, session logoff, and uninstall. The same
deadline is passed through controller release and plugin restoration; WSGM does not stack a second
set of phase budgets. WSGM-owned virtual-target and HidHide cleanup still runs after an unverified
plugin response, and the compact result is logged as clean, unverified, timed-out, or failed.

Unexpected host exit retries once or twice under one restart policy, then faults Device Integration
for the run with a clear manual retry. The fail-open path restores usable input and removes only
WSGM-owned state. It never starts, stops, kills, or reconfigures MSI Center, Handheld Companion, or
another external manager.

Recovery records only temporary plugin-owned state that was actually changed and could not be
restored. Persistent desired RGB/profile state remains separate. An indeterminate hardware write is
reported to the plugin owner and is never blindly retried.

## Public SDK and glyph data

`WSGM.Device.Sdk` is the one AOT-safe API shared by WSGM, DeviceHost, plugins, and Device Lab. It
contains the exact API/wire version, one plugin lifecycle, practical semantic capability
descriptors/state/commands/results, canonical controller and motion samples, haptic output, OEM
events, glyph data/control maps, and a publication sink.

The SDK does not contain implementation modules, generic resource leases, WSGM UI policy,
source-arbitration projections, evidence IDs/locks, source generators, Steam selectors, or CDP
patches. Add an abstraction only when the Claw plugin and a materially different plugin both need
it.

Glyph artwork and semantic control maps are static plugin data. WSGM validates local paths, IDs,
formats, dimensions, sizes, and references, then owns every Avalonia and Steam adaptation. Missing,
ambiguous, or mismatched profiles retain native Steam/generic WSGM presentation.

## Controller management

`ControllerManager` is the one WSGM-side owner of controller management for a session: the virtual
target and its replacement, the haptic return path, WSGM's own HidHide delta, the local UI capture,
the source WSGM's own surfaces navigate from, and the make-safe handoff. `DeviceCoordinator` keeps
the plugin conversation; the manager orders both halves. Nothing else creates a target, mutates
HidHide, or decides where UI input comes from.

The target is chosen by exactly two stored layers: one global default plus per-application
overrides, both kept directly under device integration rather than under a per-device profile.
Overrides are keyed by the canonical running-application identity from the one
`RunningApplicationMonitor`, which also resolves the RTSS profile, so the controller target and the
performance profile can never disagree about which application is running. The semantic capabilities
keep their five desired-state layers because hardware limits genuinely differ on battery and per
profile; a controller target does not.

Only one target exists at a time. A per-application change is one replacement operation that
neutralizes and removes the old target before creating the new one, so the two are never enumerated
together. Any unavailable prerequisite — closed release gate, missing or incompatible backend,
unhealthy HidHide, a target that does not enumerate — fails open: the shell, SDL input, and the
Steam Input lease continue unchanged, global HidHide state is untouched, and WSGM's own surfaces
stay on the SDL-plus-Steam-lease source.

Capture by a WSGM surface is reference counted and never reaches the virtual target. Controls held
when a surface opens are suppressed until released, and forwarding resumes only on the first sample
in which every control the UI used is up, so the press that opened or closed a surface never arrives
in the game as a fresh input.

The make-safe handoff is stated in the shared `ControllerHandoffStep` wire vocabulary rather than a
second WSGM-local one, so a pasted log settles how far the handoff got. WSGM's half collapses into
two of those steps and keeps the two orderings that prevent a defect as explicit guards: the virtual
target may not be removed until the physical release has concluded either way, and WSGM's HidHide
entries may not be removed until the target is gone. Removing them earlier would expose a device the
plugin is still holding, which is the duplicate-input state the single-target rule exists to
prevent. An unverified or failed plugin answer still runs WSGM's removal; the result records
`ReleasedUnverified` rather than presenting a timeout as a clean release.

Controller management remains excluded from the release: the reviewed HIDMaestro profile does not
carry the four distinct rear controls or the stick-touch fields WSGM's controller contract requires,
and exact signed driver reproduction is not established. `HidMaestroProductionBackend` implements
that as a capability-specific failure and never loads HIDMaestro, launches a helper, installs a
driver, or creates a target. `third_party/controller/README.md` holds the pinned sources and the
gate.

## Device Lab and UI ownership

Device Lab is one optional developer-tools application with GUI and CLI modes over the same internal
operations: doctor, inventory, capture, inspect/compare/correlate, fixture extraction, scaffold,
glyph import, local plugin run, validate/test, and pack.

Read-only is the default. One explicit attended action may invoke plugin-owned
snapshot/readback/restore code; it has no `--yes`, bulk, CI, imported recipe, trial-hash, receipt,
evidence-promotion, or remembered-consent route. Every output path is explicit, privacy redaction is
mandatory, and tools never read or write live `%LOCALAPPDATA%\WSGM` data.

Device Lab validates the package as data and the new state path, then atomically creates the same
machine-wide owner-mutex object used by WSGM. It keeps that unowned handle open through plugin
cleanup and disposal, so owner absence cannot become stale between detection and activation. If
construction starts but cannot return a disposable instance, or later plugin disposal fails, Device
Lab still unloads the collectible plugin context but retains the owner handle until process exit.
The still-running Device Lab therefore cannot overlap those unverified resources with a competing
WSGM cycle. Elevation, local attendance, CI refusal, and immediate confirmation are also checked
before the selected plugin assembly or its constructor loads. Only then may the plugin perform exact
read-only detection; a mismatch still refuses activation.

Settings owns startup/integration/controller-ownership/logging/update configuration and
owner-process requests. Live power, fan, controller, motion, OEM, lighting, glyph, performance, and
recovery state belongs on the overlay's Device destination. Overlay, Settings, native QAM, and
diagnostics consume the same runtime services rather than parallel policy/projection stacks.
