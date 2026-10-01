# Device plugin system

The mechanism reference for how WSGM hosts a device plugin: the package on disk, how it is
discovered, validated, installed and loaded, the lifecycle WSGM drives it through, what happens to
each publication, how commands travel back to hardware, and how the controller, haptic, OEM,
settings, profile and glyph paths are wired. File names appear where a reader has to go to the code.
The reasons behind these mechanisms and the device findings are in
[device integration](device-integration.md).

Read it together with:

| Document                                                 | Holds                                                                |
| -------------------------------------------------------- | -------------------------------------------------------------------- |
| `src\WSGM.Device.Sdk\docs\reference.md`                  | Every SDK type, rule and limit: the contract a plugin links against. |
| [device-integration.md](device-integration.md)           | The decisions and device findings behind the runtime.                |
| [device-plugin-authoring.md](device-plugin-authoring.md) | The author workflow, and the device projects in this repository.     |
| [decisions.md](decisions.md)                             | The standing product decisions.                                      |

## 1. Components and ownership

The Shell's common `PluginHost` reserves the Device category and drives its
`DevicePluginCompatibilityAdapter`. The coordinator keeps the device-specific controller release
ordering shown below; the adapter delegates to the existing runtime. Common instance deadlines,
retained failed slots and generation-checked health are in
[common plugin contracts](plugin-system.md).

```text
                     WSGM.exe (one ShellSession per interactive session)
  ┌──────────────────────────────────────────────────────────────────────────────────────┐
  │ Program.Main ── cardinality gate (counts package roots, refuses > 1)                  │
  │ ShellSession ── creates at most one DeviceCoordinator (Shell\DeviceCoordinator.cs)    │
  │   ├─ Global\WSGM.DeviceOwner marker (process lifetime)                               │
  │   ├─ DevicePluginRuntime (Shell\DevicePluginRuntime.cs)                              │
  │   │    ├─ PluginPackageLoader + PluginLoadContext (Shell\PluginPackageLoader.cs)     │
  │   │    ├─ DirectPluginHostAdapter  ← the plugin's IPluginHostAdapter                 │
  │   │    └─ IDevicePlugin instance   ← the package's entry type                        │
  │   ├─ DeviceCapabilityRouter (Shell\DeviceCapabilityRouter.cs)   descriptors/state/commands│
  │   ├─ PluginSettingsCoordinator                                   declared settings   │
  │   ├─ DeviceOemActionRouter (Shell\DeviceOemActionRouter.cs)      OEM buttons → actions│
  │   ├─ ControllerManager (Shell\ControllerManager.cs)              VIIPER target, HidHide│
  │   │    ├─ ManagedControllerRouter / ViiperControllerBackend (Input\)                  │
  │   │    ├─ HidHideOwnership (Shell\HidHideOwnership.cs)                                │
  │   │    └─ PluginHapticSink (Shell\PluginHapticSink.cs)                                │
  │   ├─ PhysicalGlyphCatalog (Core\PhysicalGlyphCatalog.cs)         glyph profiles      │
  │   └─ DeviceCoordinatorDiagnosticsServer                          named-pipe snapshot │
  │ DeviceOverlayBridge (Shell\DeviceOverlayBridge.cs) ── overlay Device destination      │
  │ SettingsViewModel / PluginSettingsPage ── plugin settings, profile authoring          │
  │ NativeQamSemanticServices / AutoTdpService ── consumers of the same router            │
  └──────────────────────────────────────────────────────────────────────────────────────┘
```

Ownership follows decision D08. The plugin owns exact identity, transports, ranges, write and
readback, restoration, physical-controller acquisition, input normalization, output encoding, OEM
event sources and static glyph data. WSGM owns session policy, semantic UI and state, desired values
and profiles, the runtime's lifetime, the virtual target, its own HidHide changes, input
arbitration, RTSS, CEF/QAM, AutoTDP and OEM action mapping. Nothing in WSGM exposes a raw WMI, HID,
EC, IOCTL, ACPI, MMIO, MSR or serial broker to a plugin.

## 2. The package on disk

A package is one `.wsgmpkg` file, a ZIP archive that WSGM reads without unpacking:

```text
<id>-<version>.wsgmpkg
  plugin.wsgm.json                     identity, entry point, hardware, capabilities, wsgmVersion
  <EntryAssembly>.dll                  AMD64 managed assembly with a CLR header
  *.dll                                package-local managed dependencies (host-first rule, §7)
  LICENSE.txt, THIRD_PARTY_NOTICES.md, PROVENANCE.md   as the package's licences require
  glyphs/profiles/<profileId>.json     glyph profiles (optional)
  glyphs/assets/<assetId>.svg|png      artwork named by its manifest id (optional)
```

Every installed package, device and common alike, is a file directly in
`%ProgramFiles%\WSGM\Plugins` (`WSGM.Install.InstallLayout.Plugins`). Installing one means copying
the file there, which only an administrator can do. A blank Program Files answer from Windows throws
rather than falling back.

Budgets applied when a package is opened (`Core\PluginPackageFile.cs`) and when Device Lab validates
or packs one (`PluginPackageWorkflow`):

| Budget        | Value                                                             |
| ------------- | ----------------------------------------------------------------- |
| Archive       | 512 MiB on disk                                                   |
| ZIP entries   | 1024                                                              |
| Files         | 512                                                               |
| One file      | 128 MiB                                                           |
| Whole package | 512 MiB uncompressed                                              |
| Manifest      | the SDK's 256 KiB document limit                                  |
| Entry names   | relative, `/`-separated, no `.` or `..` segment, no drive, no `\` |
| Duplicates    | no two entries whose names differ only in case                    |
| Native images | none: every `.dll`, `.exe` or `.sys` must carry a CLR header      |

Packages carry managed code only because WSGM loads them from memory, where a native image cannot be
loaded. A plugin that needs a system DLL, such as the Claw's `ControlLib.dll` from the Intel driver,
resolves it through the normal system search path.

## 3. Discovery

`Core\PluginPackageCatalog.Discover` reads every `*.wsgmpkg` directly in the Plugins folder, at most
128, and never loads code. Each file is opened and validated (§5), then closed again.

- The manifest's shape decides the category: a manifest with a `category` member is a common plugin
  (`plugin-system.md`); one without is a device package read by the Device SDK.
- Files with the same id: the highest version is selected. The others are listed as superseded and
  are never deleted, because WSGM does not remove a file it did not place. Dropping a newer version
  beside the old one therefore works as an update at the next start.
- One device id: that package is the candidate.
- More than one device id: `multiple-device-packages`. Device integration stays passive and the
  overlay lists every file; WSGM itself starts normally.
- A file that cannot be opened or fails validation is a catalog error with its reason. It is not a
  device candidate.
- A package whose `wsgmVersion` is missing or names another WSGM release is refused as a catalog
  error that names the version it was built for. Packing stamps the field (`eng\pack-device.ps1`,
  `eng\package-plugin.ps1`); a source manifest never carries it.

The catalog is read at every device cycle start, at every common-plugin reconcile, when Settings
opens, and for the overlay's prerequisite banner. A package copied in while WSGM runs is loaded at
the next start: a loaded package file is held open (§7) and cannot be replaced underneath WSGM.

## 4. Machine-wide synchronization

| Object                    | Kind                                                   | Held by                                                                                           | Purpose                                         |
| ------------------------- | ------------------------------------------------------ | ------------------------------------------------------------------------------------------------- | ----------------------------------------------- |
| `Global\WSGM.DeviceOwner` | named mutex created unowned; ownership is `createdNew` | the `DeviceCoordinator` for the process lifetime, setup and uninstall, an attended Device Lab run | At most one hardware cycle on the machine.      |
| `Local\WSGM.Shell`        | named mutex, initially owned                           | the shell instance                                                                                | One shell per session; the installer probes it. |

The owner marker is never waited on: it is created unowned, and "already exists" means someone else
owns the hardware. The installer performs the same election with `CreateMutexW` and
`ERROR_ALREADY_EXISTS`. When the coordinator finds it already exists it logs
`Device cycle: machine-wide ownership is already active or unavailable; no cycle started.` and no
cycle ever starts.

There is no package-slot lock. A package file is replaced only while WSGM is closed, and a loaded
file is held open read-only, so nothing can change it under a running cycle.

## 5. Package validation

`PluginPackageFile.Open` refuses the package at the first failure:

1. The path ends in `.wsgmpkg` and is a plain file, not a link or a directory.
2. The archive is within the size budget and is a readable ZIP.
3. Every entry passes the name, count, size, duplicate and native-image rules (§2), and each entry's
   declared length matches the bytes read.
4. `plugin.wsgm.json` exists at the root and passes the reader for its category (size, depth, shape,
   field rules).
5. The manifest's entry assembly exists at the package root.

The catalog then validates the selected device package and reports it with a stable code:

| Check                                                                     | Code                       |
| ------------------------------------------------------------------------- | -------------------------- |
| `apiVersion` equals `DeviceApi.Version` (10)                              | `api-incompatible`         |
| Entry is an AMD64 image with a CLR header, metadata and assembly manifest | `architecture-unsupported` |

Several device ids yield `multiple-device-packages`; none yields `no-package-installed`. An invalid
device package is shown in the overlay's Diagnostics section. Validation never loads the assembly.

## 6. Installing, replacing and removing

- Install: copy `<id>-<version>.wsgmpkg` into `%ProgramFiles%\WSGM\Plugins`, then start WSGM.
- Replace: close WSGM, copy the new file in (remove the old one, or leave it to be superseded), then
  start WSGM.
- Remove: close WSGM and delete the file.

WSGM Settings' Plugins page does the same through the UI. Install copies a package that the
installed release bundles (`%ProgramFiles%\WSGM\Setup\Packages`, checked against the recorded
`%ProgramData%\WSGM\bundle.json`) into the Plugins folder, and offers a device package only when its
hardware rules match this machine and no other device package is installed. Remove deletes the file;
a loaded file cannot be deleted, so it is listed in `%ProgramData%\WSGM\plugin-removals.json` and
deleted before the next discovery. Both apply at the next start. A package that needs a missing
system component points to setup's repair, which installs it.

`eng\dev-deploy.ps1` stages the Claw package from this checkout, copies it in from an elevated child
beside the target, renames it into place and deletes every other build of the same id.

## 7. Loading the plugin

`Shell\PluginPackageLoader.cs` reopens the selected file, requires its manifest to equal the one
discovery admitted (a file swapped in between is refused), and loads the package into a collectible
`AssemblyLoadContext` named `WSGM.Plugin:<package id>`.

- The file stays open with `FileShare.Read` until the context is unloaded, so it cannot be replaced
  or deleted while its code may run. Assemblies and their symbols are loaded from memory.
- The entry type must be public, concrete, non-generic, assignable to `IDevicePlugin`, with a public
  parameterless constructor. Its `PackageId` must equal the manifest `id`.
- Host-first resolution: the SDK assembly, `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` are
  always answered from the host regardless of version. Every other assembly is asked of the default
  context first; the package copy (`<name>.dll`, or `<culture>/<name>.dll` for a satellite) is
  loaded only when the host has no copy or cannot satisfy the version, and that duplicate is logged
  once. Native resolution is left to the system. The reason is in `device-integration.md`,
  "Host-first dependency resolution".
- A load failure disposes the plugin if it was created, unloads the context, closes the file and
  rethrows.
- Glyph profiles are imported from the package file through the SDK's `IGlyphPackageSource`
  contract.
- Unload is requested, not verified. `DevicePluginRuntime.DisposeAsync` calls `Unload()` only when
  command quiescence, the emergency stop and the plugin's `DisposeAsync` were all clean; otherwise
  the context stays loaded. Nothing waits for the GC to collect it. A plugin change applies at the
  next start; there is no hot upgrade.

The context is not crash containment: a process-fatal plugin failure terminates WSGM (decision D03).

## 8. The device cycle

`DeviceCoordinator` serializes every transition through one gate. There is no dedicated thread;
background work is tracked and awaited at shutdown, and router publications are posted to the UI
thread with a revision check so stale snapshots are dropped.

### States

The host-owned `DeviceCycleState` is logged on every change as
`Device cycle: state=<state>, cycleGeneration=<n>.`:

| State                 | Entered when                                                                                                                                |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- |
| `Disabled`            | Integration off, or a cycle ended intentionally.                                                                                            |
| `Detected`            | A cycle start began; the Plugins folder is about to be read.                                                                                |
| `Passive`             | No valid package, or the plugin did not match the machine.                                                                                  |
| `Activating`          | The runtime is loading, or a restart is scheduled.                                                                                          |
| `Active` / `Degraded` | The plugin's `PluginStartResult`.                                                                                                           |
| `Suspended`           | After a successful suspend.                                                                                                                 |
| `Deactivating`        | During an intentional stop.                                                                                                                 |
| `Faulted`             | Restart attempts exhausted, or a restart failed. Fails open: the virtual target and WSGM's HidHide entries are gone; desired state is kept. |

### Start

A cycle starts when integration is enabled at construction, when the master toggle turns on, after a
fault backoff, or on manual retry:

1. State `Detected`. Collect `DeviceMachineIdentity` from the registry SMBIOS keys and run discovery
   (§3); a failure schedules a start fault. No valid package sets `Passive` and logs
   `Device cycle passive: <code>; devicePackages=<n>.`.
2. Advance the cycle generation; state `Activating`; load the package (§7).
3. Attach the runtime to the coordinator, the capability router, the OEM router and the settings
   coordinator. Then allowlist WSGM in HidHide before the plugin starts, because a plugin cannot
   discover a controller that another tool's allowlist hides from WSGM.
4. `client.StartAsync` with a 15 s deadline: `DetectAsync` (no match publishes `Passive` with the
   plugin's reason), then `StartAsync` with the host adapter, generation, definition id, the state
   directory `%LOCALAPPDATA%\WSGM\DeviceState\<packageId>` and the controller-management flag. A
   plugin exception publishes `Degraded` with `TransportFaulted` and rethrows.
5. Record the definition id, attach plugin settings, import glyph profiles, reset the restart
   counter, log `Device cycle active: package=…, cycleGeneration=…, state=…`, and observe the
   runtime's completion.

An exception from the caller's token rethrows; the runtime's own deadline becomes a `StartCanceled`
cleanup; anything else a `StartFailed` cleanup. Both run a fresh 5 s bounded stop before scheduling
the fault, and a canceled caller is never followed by an automatic restart.

### Faults and restarts

A background service failure reaches WSGM through `IPluginHostAdapter.ReportFault`: the runtime
closes command admission, cancels in-flight work and completes with `BackgroundFault`. The
coordinator tears the client down with a 15 s deadline (controller release with the physical pad
kept hidden, `StopAsync(RuntimeFault)`, detach, dispose), and then:

- an unverified step is logged
  (`Device plugin fault cleanup had unverified steps; restarting anyway`) and never blocks the
  restart;
- an intentional stop, disposal, or integration off sets `Disabled`;
- otherwise it logs `Device plugin fault: generation=…, reason=…, detail=…` and schedules a restart.

Restarts are bounded to two attempts with backoffs of 1 s and 4 s
(`Device plugin restart n/2 scheduled in x s.`). Exhaustion sets `Faulted`, logs
`Device cycle faulted after restart exhaustion` and shows the physical pad again. Manual retry from
the overlay's recovery row works only from `Faulted`.

### Suspend and resume

Session lock and system suspend trigger suspend; unlock and resume trigger resume. The system events
reach the shell's message-only window only because `MessageWindow` registers for them with
`RegisterSuspendResumeNotification`; Windows broadcasts the suspend and resume codes to top-level
windows alone, and until 2026-09-22 nothing was registered, so no sleep ever suspended or resumed
the cycle and the Claw's lighting came back from standby at the firmware default. The shell
edge-triggers and serializes them, so overlapping lock and sleep events collapse.

Suspend: cancel a controller start still in flight, then, while management is `Active`, block
forwarding (`Controller forwarding paused for suspend; the virtual controller is kept.`). The
virtual controller and the hidden pad stay as they are; nothing is released. Then
`client.SuspendAsync`, which closes the plugin's own devices, and reset the OEM router.

Resume: `DecideResume` resumes a cleanly suspended plugin in place and replaces anything else with a
fresh cycle (see `device-integration.md`, "Sleep"). In place means re-collect identity, advance the
cycle generation, `client.ResumeAsync` (the runtime requires `Suspended` and a strictly greater
generation), synchronize the generation into the router and OEM router, and then
`ControllerManager.ResumeForwardingAsync`, which clears the forwarding block and logs
`Controller forwarding resumed: system wake.` or `: session unlock.`. The first clean sample then
re-arms the kept target. A plugin resume that fails after a sleep falls through to a fresh cycle.

### Stop and shutdown

Turning integration off stops with reason `IntegrationDisabled`; shutdown maps the application
reason to `Updating`, `SessionEnding`, `Uninstalling` or `WsgmExiting`. The order is fixed and every
step's failure is retained while cleanup continues: close command admission, state `Deactivating`,
controller release with `FullDeactivation` (§12), `client.StopAsync`, detach, dispose, state
`Disabled`. A verified teardown means no step threw and the stop reported `Clean`
(`Device hardware release: Disabled, verified.`); anything else is logged as
`Device teardown had unverified steps: …` and the shell continues.

Shutdown cancels the coordinator's lifetime before waiting for the transition gate, so an in-flight
start unwinds under the shutdown owner's deadline rather than stacking a second budget.

### Controller management toggled inside a cycle

Disable: controller release with `ControllerOnly`, then `SetControllerManagementAsync(false)`; if
the plugin does not acknowledge, the cycle is stopped as `RuntimeFault` and restarted with the
persisted policy. Enable: allowlist WSGM in HidHide, then `SetControllerManagementAsync(true)`. The
cycle generation does not change: turning the controller on is not a new device, and advancing it
here left the OEM services publishing a generation the router had moved past.

### Deadlines

Every budget below is a `Deadline` on the SDK's `ActiveClock`, which does not count time the process
spent frozen by Modern Standby, so an operation mid-flight when the machine sleeps keeps its budget
across the wake. The shutdown handshake with the installer is the one wall-clock budget; it is
converted at the boundary with `Deadline.At`.

| Phase                                                        | Budget                      |
| ------------------------------------------------------------ | --------------------------- |
| Slot gate at startup, cycle start and maintenance            | 5 s                         |
| Runtime start (Detect + Start)                               | 15 s                        |
| Suspend, resume                                              | 5 s                         |
| Controller-management toggle                                 | 6 s                         |
| Stop for disable, shutdown, update, uninstall, runtime fault | 15 s                        |
| Cleanup after a canceled or failed start                     | 5 s                         |
| Runtime emergency cleanup on dispose                         | 5 s                         |
| Restart backoff                                              | 1 s, then 4 s; two attempts |

## 9. Publications from the plugin

`DirectPluginHostAdapter` validates generations and raises one event per channel; the consumer
validates content. Every consumer runs synchronously on the publishing thread, and a throwing
consumer is logged under `Log.Change("device-plugin-publication-<channel>")`.

| Channel                                | Adapter rule                                                                                                   | Consumer and its rules                                                                                                                                                                                                                                                                                                                                          |
| -------------------------------------- | -------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Descriptor set                         | `CycleGeneration` must be current; `Generation` must increase; the adapter records it.                         | `DeviceCapabilityRouter`: at most 128 descriptors; at most 16 sections, each valid and unique; every descriptor valid (§9.1); placement valid; keys unique. Acceptance replaces descriptors and sections and clears states, pending values, last results and availability. Rejection logs `Device descriptor set rejected: <error>` and keeps the previous set. |
| Capability state                       | Current cycle generation and the exact current descriptor generation; the adapter stamps a monotonic sequence. | Router: descriptor must exist; state must validate (generations, value shape, `Verified` requires a readback value); sequence must increase. Availability transitions log once per change.                                                                                                                                                                      |
| Physical devices + haptic capabilities | none                                                                                                           | `PluginHapticSink.Publish`, then `ControllerManager.StartAsync` (§12).                                                                                                                                                                                                                                                                                          |
| Controller sample                      | none: a sample carries no generation or sequence.                                                              | `ControllerManager.Submit`: one-slot latest-wins pump (§12).                                                                                                                                                                                                                                                                                                    |
| OEM controls                           | none                                                                                                           | `DeviceOemActionRouter`: at most 16, valid unique ids, valid display; else rejected whole.                                                                                                                                                                                                                                                                      |
| OEM event                              | none                                                                                                           | OEM router suppression rules (§13).                                                                                                                                                                                                                                                                                                                             |
| Settings manifest                      | `TryValidate` must pass; a failure traces `Settings manifest refused` and keeps the previous manifest.         | `PluginSettingsCoordinator` caches the declaration and pushes the resolved values back through `ApplySettingsAsync`.                                                                                                                                                                                                                                            |
| Trace                                  | Truncated to 1024 characters; scope defaults to `plugin`.                                                      | Written as `plugin/<scope>: <message>` at the given level.                                                                                                                                                                                                                                                                                                      |
| ReportFault                            | Traces at Error, then completes the runtime with `BackgroundFault` (§8).                                       | A fault after teardown is only logged under `device-plugin-late-fault`.                                                                                                                                                                                                                                                                                         |

### 9.1 Descriptor rules the router enforces

Beyond the SDK's own `TryValidate` methods, `DeviceCapabilityValidation` requires:

- capability id an identifier of at most 128 characters, instance id at most 64, valid display,
  section and category ids within the SDK bounds;
- at least one of read, write or action; `ValueKind.None` exactly when `SupportsAction` and never
  readable or writable;
- integer descriptors with a minimum, a maximum, minimum ≤ maximum and a positive step;
- choice descriptors with 1 to 64 unique identifier values, and no choices on any other kind;
- text descriptors with a maximum length of 1 to 256, and none elsewhere;
- a role whose value kind matches: `FanCurve` is `Curve`, `LightingZoneColor` is `Color`, the power
  limits and `GenericRange` are `Integer`, `Telemetry` and `GenericReadOnly` may be boolean,
  integer, choice or text.

Placement: a descriptor naming an undeclared section is refused unless its role is generic, in which
case it falls back to a WSGM-owned home; a category must belong to the named section.

### 9.2 Freshness

Every snapshot re-evaluates each state against a role-based maximum age:

| Role                          | Maximum age |
| ----------------------------- | ----------- |
| `Telemetry`, `FanMeasuredRpm` | 5 s         |
| Charge and lighting roles     | 5 min       |
| Everything else               | 30 s        |

A state from another cycle generation becomes `Stale` with `GenerationChanged`; an expired one
`Stale` with `ObservationExpired`; `Faulted` and `Unknown` are left alone. A descriptor with no
state yet projects as unavailable with `ObservationExpired`. While the router is detached every
state is `Stale` with `HostUnavailable`.

## 10. Commands

Every write or action funnels through
`DeviceCoordinator.ExecuteCapabilityAsync(capabilityId, instanceId, value, timeout, origin)`. All
production callers pass a 5 s timeout: the overlay, Settings, the native QAM and the
variable-refresh toggle (`User`), AutoTDP and authored profile application (`AutomaticControl`), and
the per-application power and variable-refresh restore (`ProfileRestore`). Desired-value
reconciliation uses `DesiredStateRestore`: an applied sustained limit pauses AutoTDP through the
non-persisting assignment callback. Only `User` commands may enter configuration persistence (§11).
Restore completion, readback and firmware defaults never become new user preferences.

The router holds one `SemaphoreSlim(1,1)` per capability key. Preflight builds the
`CapabilityCommand` with a fresh id, the expected descriptor and cycle generations and
`Deadline = Deadline.After(timeout)`, then refuses with `Rejected` in this order:

| Condition                                         | Reason                           |
| ------------------------------------------------- | -------------------------------- |
| Not connected                                     | `HostUnavailable` (retryable)    |
| No descriptor                                     | `Unsupported`                    |
| No state yet                                      | `ObservationExpired` (retryable) |
| State not available, or not `Observed`/`Verified` | the state's own reason           |
| Unavailable on the current power source           | `UnavailableOnPowerSource`       |
| Action on a non-action, write on a read-only      | `Unsupported`                    |
| Value outside the descriptor                      | `ValueOutOfRange`                |

A curve must have 1 to 64 points with strictly ascending inputs and outputs within the declared
bounds; an undeclared bound is not invented. A passing write records the pending value.

`DevicePluginRuntime.ExecuteCommandAsync` requires `Active` or `Degraded`, open admission and a
unique command id, then calls the plugin under a token that fires at the command deadline, the
runtime's lifetime, or the caller's cancellation. If the token fires while the plugin is still
working, the runtime answers immediately with `TimedOut` (deadline passed) or `Indeterminate`
(canceled earlier), reason `Quiescing`, and hands the plugin's still-running task back as a late
completion. The router keeps the pending value, observes the late task, and applies its result only
if it is still attached to the same runtime and generation (`Late device command result reconciled`
versus `ignored`). A plugin exception maps to `Indeterminate` with `TransportFaulted`; a mismatched
command id becomes `Uncertain`.

Terminal results clear the pending value, are stored as the capability's last result, and log
`Device command: capability=… command=… outcome=… rollback=…`. The user sees `Pending`, `Completed`
(`AppliedVerified` or `AppliedUnverified`), `Uncertain` (`TimedOut` or `Indeterminate`) or `Failed`
(`Rejected`). Uncertain writes are never retried automatically.

One side effect: a `User` write of an integer to the `PowerSustainedLimit` role that applied pauses
AutoTDP (`AutoTDP paused: the sustained power limit was set to n W by hand.`) and persists the watts
to the profile layer in force ([profiles](profiles.md)).

Debouncing lives in the controls, not the router: the slider commits 250 ms after the last change,
and the colour editor writes only on Apply (colour, then brightness).

## 11. Desired state, settings and profiles

### Desired values

A capability's desired value is its profile value: the running game's enabled profile, then Global,
then none. The model and every rule about it are in [profiles](profiles.md). The values live in
`Profiles.*.Device[]`, keyed by the machine's identity key, so swapping plugins keeps the machine's
preferences. `DeviceCapabilityRouter.UpdateDesiredContext` indexes them for the running application,
and `CapabilityProjection.DesiredSource` says which layer supplied each one. Reconciliation lowers
limits first when lowering, raises the fast limit first when raising, skips values equal to the
readback, and logs one summary line.

`DeviceCoordinator.PersistUserCapabilityValueAsync` saves every `User` write the device accepted
through `ProfileService.SetDeviceAsync`, into the layer in force. Every profile change, including
the per-game switch and a value reset to Global, reaches the device through
`DeviceCoordinator.ApplyProfilesAsync`, which reconciles every capability.

Two roles are deliberately excluded from `Device[]` because they have typed profile values and their
own release rules: `PowerSustainedLimit` and `VariableRefreshRate`. Their manual writes reach
`ApplicationPerformanceReconciler` through `AttachAutoTdpManualOverride` and
`AttachManualVariableRefreshOverride`, so the overlay row and Steam's own control save to one place.
Reconciliation uses a separate origin and never enters either persistence funnel. A user control
landing on its existing desired value needs no configuration write.

Every transition of the device cycle to active, including resume, restores every desired value once.
Lighting readiness also restores lighting, with at most three attempts per zone, value and cycle: a
refused command may be retried. An uncertain write is never repeated automatically for the same
value, and no readback is waited for; a different desired value, such as another game's profile, is
a new write and goes ahead. Pending commands block automatic restoration. The saved value remains
intact after every failure. Diagnostics report
`Device restore <capability>/<instance> from <layer> (<reason>): outcome=…, attempt n of 3.` and the
`Desired-value reconciliation (…)` summary. A manual command suppresses readiness restoration while
its hardware result and desired configuration are being committed.

During resume, the capability router accepts the attached runtime's new cycle before validating its
first descriptor publication. Descriptor numbering can restart within that cycle. The coordinator's
later synchronization preserves the freshly accepted state.

### Plugin settings

A declared setting is a preference WSGM stores under `PluginSettings[]`, keyed by device definition
and plugin id, with the cached declaration beside the values. On every configuration apply and on
every manifest publication the stored values are re-resolved against the current declaration; a
value that no longer validates falls back to the default and logs
`Plugin setting '<id>' fell back to its default`. The complete resolved set is delivered to
`ApplySettingsAsync`. The Settings page draws the declaration: integers clamp to the declared range,
text truncates to its maximum, colours are masked to 24 bits, and a setting naming an unknown
section renders in a fallback section.

### Authored profiles

A profile is a named curve or colour the user builds in Settings for `fan.curve` or
`lighting.zone-color`. The fan curve in force is the `FanCurveProfileId` profile value, chosen in
the overlay and resolved like every other value. `Core\DeviceProfileValidation.cs` checks a curve
against the live descriptor at apply time (`CapabilityAbsent`, `NotACurve`, `PointCount` 1–64,
`NotAscending`, `OutOfBounds`). `Shell\DeviceProfileApplier.cs` validates, builds the curve value
and executes with a 5 s timeout, counting `AppliedUnverified` as success and a timeout as failure.
Deleting a profile clears every layer that selected it, so that layer falls back to the one below.
Curve editing goes through `CurveEditing` (at most 64 points, minimum input gap 1, a 0–100 plane),
so an invalid curve cannot be built. The reasons are in `device-integration.md`, "Authored
profiles".

## 12. Controller management

`ControllerManager` is the one owner of the virtual target, its replacement, the haptic return path,
WSGM's HidHide delta, UI capture, the managed pad WSGM's own surfaces read and the controller
release. Its states are `Off`, `Unavailable`, `Idle`, `Active` and `Faulted`. While `Active`, the
overlay's `GamepadService` polls `ManagedUiPad` on its UI-thread tick in place of the SDL pads; in
every other state it reads SDL plus the Steam Input lease.

### Start

Management starts when the plugin publishes physical devices, not at cycle start:

1. Store the devices and selection; return `Off` when management is disabled.
2. `ViiperControllerBackend.DiscoverAsync` must report ready with capabilities, else `Unavailable`.
3. Resolve the target from the `ControllerTarget` profile value for the running application
   (`SteamDeckComposite` when no layer sets one).
4. `HidHideOwnership.HideAsync` allowlists WSGM, hides every identity marked `RequiresHiding` and
   turns the cloak on when it is off; a refused write or inverse mode means `Unavailable`.
5. Keep an existing target of the same kind (recreating it made Steam see the pad unplug and
   re-attach on every wake), or create the target, and activate the source; failure sets `Faulted`.

When management is enabled but the result is not `Active`, the physical pad is shown again. The
coordinator logs every result as `Controller management: state=…, target=…, source=…, detail=…`.
Every unavailable prerequisite fails open: the shell, SDL input and the Steam Input lease continue
unchanged.

### Samples

`Submit` drops a sample after disposal (`Log.Change` key `controller-sample-after-dispose`), then
overwrites the single pending slot and wakes the drain worker. Newer samples replace unread ones;
nothing queues. Routing raises the unfiltered diagnostic event and writes the buttons to
`ManagedUiPad`, then withholds the sample from the game (captured, forwarding blocked, or not yet
resumable) or sends it to the target. Before the target sees it, `ManagedControllerSampleValidator`
checks only the values: sticks within −1…1, triggers within 0…1 and finite motion. A sample carries
no generation, sequence or quality to check, because each one is the full state and a late one is
corrected by the next. A failure neutralizes the target and logs `managed-controller-neutralized`.

### Targets and encoders

VIIPER binds `libviiper`, listens on `127.0.0.1:0`, bus 1, and creates a target as add, open, submit
a neutral frame, register the feedback callback, attach. Bus 1 is created once and lives until
shutdown; the fork keeps a C API bus while it is empty, so a release that removes the pad and a
restart that adds the replacement seconds later find the same bus (`external\controller\viiper.md`).
The Steam Deck target sends the 64-byte Neptune state: buttons at bytes 8–14, pads 16–23, motion
24–35 with accelerometer counts of 16384 per g and gyro counts of 16 per degree per second on the
`X, -Z, Y` axes, triggers scaled to 32767, sticks clamped to the signed range, forces at 56–63. Xbox
360 maps the standard buttons, byte triggers and signed sticks; DualShock 4 additionally maps touch
contacts, gyro and acceleration. The target is replaced as one neutralize, remove, create operation,
and the usbip-win2 client attachment is plugged out by port before the server device is deleted.

### Haptic return path

VIIPER calls the feedback callback on a library thread. For the Deck target:

| Report | Meaning                                                                                              |
| ------ | ---------------------------------------------------------------------------------------------------- |
| `0xEB` | rumble: two 16-bit values over 65535                                                                 |
| `0xDC` | haptic event: byte 3 (0 stop, 1 half, else full) with a 150 ms stop                                  |
| `0xEA` | trackpad haptics: stop after 35 ms                                                                   |
| `0x8F` | pulse: `min(255, count·16 + report[9]) / 255`, stopping after `period·count` ms clamped to 1…5000 ms |
| `0xE2` | gain: ignored                                                                                        |

Unknown command ids are logged at most four times each. `ControllerOutputRouter` queues into a
channel of capacity 1 that drops the oldest, and only while a target is attached. A frame carries no
target generation: it is the whole motor state, and a newer one replaces an older one. The run loop:

- drops a frame when no target is attached, its kind is not the target's, the sink is not owned, a
  channel is not finite within 0…1, or a pulse has no positive stop time; then clamps unsupported
  channels;
- floors bounded events (not continuous rumble) to the plugin's `MinimumStartIntensity` and
  stretches their stop to at least `MinimumPulse`;
- paces at `1 / MaxFramesPerSecond` clamped to 1…1000 fps, never holding back a stop, applies
  through `PluginHapticSink`, and schedules the pulse stop.

The one guard kept is an epoch: stopping output, attaching another target or detaching starts a new
one, and a frame that was already on its way is dropped rather than landing after the stop. A stop
sends an explicit silent frame because the physical motors latch. A failed write is logged and the
next frame is tried. `PluginHapticSink` passes frames on only while the plugin has published haptic
capabilities for the pad it reads, and the coordinator withdraws it when it detaches the runtime.
The runtime forwards to `IDevicePlugin.ApplyHapticOutputAsync` only in `Active` or `Degraded`. The
first physical output for each target is logged once, never at report cadence.

### UI capture

`UiCaptureState` holds a set of surface ids; a duplicate claim is logged and refused. The first
claim neutralizes the target and remembers the controls held at open as withheld from the game.
While captured, no sample reaches the game. After the last release, forwarding resumes only on the
first sample in which every withheld control is up. The UI side needs no mask: `GamepadService`
edge-triggers `ManagedUiPad` like an SDL pad, so a control already held when a surface opens
produces no press. The overlay's rear-button OEM action pulses `RearPaddle1` or `RearPaddle2` for
200 ms and always publishes the release.

### Controller release

`ControllerManager.ReleaseAsync` lets go of the controller in a fixed order. Each step is attempted
whatever the one before did, each failure is only logged, and nothing waits for a readback or
retries:

1. Clear the pending sample, block forwarding and neutralize the target.
2. Ask the plugin to let go (`ReleaseControllerAsync`, best effort; it returns nothing to verify).
3. Remove the target.
4. Remove WSGM's HidHide entries and turn the cloak off, unless the physical pad is kept hidden,
   which only a `RuntimeFault` stop asks for because the restart takes the pad again at once.
5. State `Off` for `FullDeactivation`, `Idle` for `ControllerOnly`, and one log line
   `Controller released: scope=…, physicalKeptHidden=…`.

A suspend does not release: it only blocks forwarding and keeps the target and the hidden pad (§8).

### HidHide ledger

`HidHideOwnership` records every entry it adds in `%LOCALAPPDATA%\WSGM\hidhide-ownership.json`
before writing it, so a crash still leaves the entry for the next exit to remove. Showing the pad
removes exactly those entries, turns the cloak off whether or not this run turned it on, and deletes
the ledger only when every write was accepted; otherwise the ledger stays for the next attempt.
Writes are trusted when the driver accepts them; nothing is compared or retried. Inverse mode is
refused. Paths compare equal across DOS and NT device notation; the findings behind that and the
pre-start allowlist are in `device-integration.md`, "HidHide findings".

## 13. OEM controls

`DeviceOemActionRouter` maps a published control's press to one WSGM action from the closed
`OemAction` vocabulary stored under `DeviceIntegration.OemAssignments`:

| Action                                                    | Effect                                                            |
| --------------------------------------------------------- | ----------------------------------------------------------------- |
| `ToggleWsgmOverlay`                                       | Toggle the overlay.                                               |
| `ToggleSteamQuickAccess`                                  | Press Steam's Quick Access button on the virtual pad (see below). |
| `ToggleSteamOverlay`                                      | Press Steam's Guide button on the virtual pad (see below).        |
| `ShowWsgmDevicePage`                                      | Open the overlay's Device page.                                   |
| `ToggleWsgmTaskbar`                                       | Toggle the Open apps strip.                                       |
| `ToggleDesktopGameMode`                                   | Enter Game Mode if Explorer runs, else Desktop Mode.              |
| `ToggleOnScreenKeyboard`                                  | Toggle the touch keyboard.                                        |
| `CyclePerformanceProfile`, `CyclePerformanceOverlayLevel` | RTSS cycles.                                                      |
| `VirtualTargetRearButton1`, `VirtualTargetRearButton2`    | Pulse a rear paddle on the target.                                |

An unassigned control resolves to `Disabled`, and the plugin exposes the front buttons to Steam as
the target's own Guide and Quick Access buttons. The one exception is a front control the plugin
marks `CompanionApplication`: the manufacturer's companion-app button that the Steam Deck layout has
no place for, such as Armoury Crate on the Xbox Ally, where the Xbox button is the Guide and Library
is Quick Access. Unassigned, it toggles the WSGM overlay, as Handheld Companion opens its own window
from it. An explicit assignment, `Disabled` included, always wins.

Assignments are authored in plugin code, and there is no UI to rebind them because WSGM does not
build a remapper. Every handheld on the market today maps cleanly onto a Steam Deck controller with
no buttons or functions left over, so a remapper would answer a problem no supported device has.
Rear-button actions are assignable only to `Rear` placement and only when the target has rear
buttons (Steam Deck); a control that `RequiresControllerAcquisition` needs management enabled.

The two Steam actions (`ShellSession.ToggleSteamSurfaceAsync`) never hand the physical pad to Steam.
While a managed target is `Active`, `ControllerManager.PressSteamButtonAsync` holds Steam's own
button on the virtual pad for 200 ms: Guide for the overlay; for Quick Access, the Quick Access
button on the Steam Deck target and Guide + A on the others. Without an active target, Big Picture's
keyboard shortcut is sent when Big Picture is visible. The Game Mode keyboard action instead invokes
Steam's native Keyboard action through CEF (`SteamNativeSurfaceCommands.ReplayAsync`) on the one
game overlay, or the visible main window.

Events carry no source generation. They are refused for an unknown control, a blank deduplication
id, a timestamp more than 5 s in the future, or one older than the 30 s deduplication window.
Release edges are logged and ignored: actions run on press only. Duplicates within the window are
suppressed through a 256-entry table. Each action runs under a 3 s budget and logs
`Device OEM action: control=…, action=…, completed=…`.

There is no assignment editor in Settings; assignments exist only when the configuration file
carries them.

## 14. Overlay, Settings, QAM and diagnostics

The overlay's Device destination (`DeviceOverlayBridge`, `DeviceOverlaySectionPages`) shows the SDK
shared Power, RGB, Controller and Info sections, followed by custom sections, dropping empty ones.
Windows energy plans keep Power available with integration off. Unplaced controls can still use
WSGM's fallback sections `Overview`, `PowerAndThermals`, `ControllerAndMotion`, `Oem`,
`LightingAndFeatures`, `Diagnostics`. An unplaced capability lands in the section its role implies.
WSGM's own rows join them: AutoTDP and the authored fan profile under power; controller target and
glyph selection, plus the glyph preview and input test, under controller; recovery under
diagnostics.

**A WSGM section whose subject the plugin already declares is not a second page.** `DeclaredKeyFor`
maps each WSGM section to the `SettingSectionKey` that means the same thing: `Power`, `Controller`,
`Lighting`, `Diagnostics`, `General`. A declared section carrying that key absorbs it: `AbsorbedBy`
folds its count and status into the declared card, and `RenderOwnedDeviceRows` draws its rows on the
declared page after the device's own. Without this a device declaring a Power section produced that
page **and** WSGM's, with the power limits on one and the frame limit on the other; the same split
gave two Controller pages. `Oem` deliberately maps to nothing: it is WSGM policy over a plugin's
controls, and no plugin has a vocabulary for that subject. The shared performance rows follow the
absorption too, so they stay on whichever page power ended up being.

| Descriptor                              | Control                                                                            |
| --------------------------------------- | ---------------------------------------------------------------------------------- |
| Writable integer with minimum < maximum | slider, committing 250 ms after the last change                                    |
| Boolean                                 | toggle                                                                             |
| Choice                                  | combo box                                                                          |
| Text                                    | text box committing on Enter or focus loss                                         |
| Writable curve                          | `DeviceCurveRow`: the curve editor plus three fan presets, committing after 400 ms |
| Colour                                  | status row opening the colour editor (spectrum, three channels, brightness, hex)   |
| Action, read-only curve, read-only      | status row; an action shows `RUN`                                                  |

The curve editor (`Controls\CurveEditor`, shared with Settings authoring) is modelled on
HandheldCompanion's fan graph: a filled plot, one draggable node per breakpoint, and the live
temperature drawn as a dashed marker where it crosses the curve. Left/Right selects a node and
Up/Down moves it, on pad and keyboard alike. Two things differ from HC, both device facts rather
than design choices. The nodes sit at the breakpoints the firmware stores (six on the Claw, not HC's
fixed eleven), and their inputs are pinned while outputs move, because those breakpoints are the fan
table. `RisingOutput` holds each output between its neighbours': the fan firmware refuses a table
whose duties dip, so a drag that would build one has to be impossible rather than reported on apply.
The three presets are HandheldCompanion's own `IDevice.fanPresets` arrays (Quiet, Default,
Aggressive), stored at HC's 11-point resolution in `Core\FanCurvePresets.cs` and interpolated onto
the device's own temperatures at apply time, so a preset never invents a breakpoint the table does
not have.

A row's value is the pending value, else the desired value, else the observed value. Its status
follows the projection: `Progress` while pending; `Faulted` on failure or `TransportFaulted`;
`Warning` for uncertain or out-of-range; `Stale` for expired or generation-changed;
`ExternallyOwned` for `ResourceConflict` or `ResourceReleased`; `Unsupported` for `Unsupported`,
`FirmwareNotVerified` or `PrerequisiteMissing`. Readback is never a precondition: an available
capability whose state is neither stale nor faulted is commandable, including one that was never
read back, which shows "Ready · no readback". A refresh is skipped while a control has focus so
telemetry cannot destroy an edit. The authored-profile row states scope: "applies to this game only"
or "applies to everything".

Settings owns the master toggle, controller management, AutoTDP, the managed target, glyph
selection, the plugin's declared settings and profile authoring. It never becomes a device control
surface (D22b). The standalone Settings process reads the coordinator's diagnostics snapshot (state,
package id and version, cycle generation, capability counts) over the named pipe
`WSGM.DeviceCoordinator.<sessionId>` with a 750 ms timeout.

The native QAM and AutoTDP consume the same router. AutoTDP takes the first writable integer
`PowerSustainedLimit`, ticks every second, and never retries an uncertain write. It additionally
requires a verified active frame-rate limit: one service availability result guards enable commands
and disables both UI controls with the same reason, and a limiter-off event relinquishes runtime
control but keeps the enabled setting, so control resumes when a limiter returns
([AutoTDP](autotdp-controller.md) has the ownership contract). The QAM's TDP control requires a
watt-unit descriptor with `1 ≤ min < max ≤ 200`.

`PairedPowerLimitId` opts a sustained descriptor into plugin-owned paired commands:

- AutoTDP sends `ApplyPowerPair` with captured cycle/descriptor generations and requires applied
  results: a verified result must read back the target, an unverified one is accepted. Without any
  value for the limit, AutoTDP starts from the descriptor's maximum.
- Both original limits are retained for release, each as last read or written, else as the profile
  asks. AutoTDP needs only a limit it can command in the current cycle: no readback, no settled
  uncertain result and no idle command lane.
- The sustained descriptor's range defines coordinated targets; the plugin owns the mapping and
  confirms both limits. The Claw maps the target to equal PL1/PL2 values through its existing
  ordered-write and rollback implementation. Other plugins may publish different companion bounds
  and steps, and host validation does not impose the Claw's equal-limit policy on other hardware.

The manual TDP preferences are four profile values: `TdpUnified`, `UnifiedWatts`, `SustainedWatts`
and `BoostWatts`. Each resolves on its own, so a game that sets one inherits the rest from Global.
These values are preferences rather than readback:

- Saved unified targets restore through the paired command with captured generations, and
  profile-owned pair release uses the same coordinated path. Split restoration validates the saved
  boost against its descriptor, applies the plugin's coordinated target and then restores the
  independent boost under one power-mutation gate. Both results must be applied (a verified one must
  read back the requested value); there is no retry after uncertainty.
- Manual sustained edits from Overlay and QAM consult the same active profile in the coordinator and
  use the active mode to select paired dispatch. Verified independent boost edits save the advanced
  boost value and select split mode while retaining unified history. Saving a unified target
  preserves the stored advanced values, and saving an advanced sustained value preserves the unified
  target.
- Overlay Device exposes an Advanced/split versus Unified selector, and QAM exposes the same mode
  toggle and one TDP slider in unified mode. Both surfaces expose the shared mode and retain
  readback. Selection persists only policy; a subsequent sustained-slider edit applies the
  coordinated target.

## 15. Glyphs

At cycle start the coordinator imports `glyphs\` through the SDK importer, logs
`Device glyph catalog: package=…, profiles=…, rejected=…`, and stores the profiles in
`PhysicalGlyphCatalog`. Selection follows the `GlyphSelection` setting: `Automatic` picks the
ordinal-first profile whose `ExactDeviceIds` contain the matched definition; `NativeSteam` disables
artwork overrides while Steam control hiding still uses the active package's automatic profile; a
manual id that does not match falls back to automatic and reports it. Any fallback leaves Valve's
glyphs untouched and the overlay draws letters.

On the Avalonia side `PhysicalGlyphService` resolves a control to a render plan (vector paths
converted to `StreamGeometry`, or the PNG bytes), authorizes navigation hints only while the managed
handheld is the input source, and caches at most 128 plans or 4 MiB, keyed by profile, revision,
control, theme and scale bucket. On the Steam side `SteamInputGlyphPresentation` maps Valve's
resource paths to `data:` URIs, `SteamGlyphCss` builds one stylesheet of `content: url(...)`
overrides, controller-image custom properties and `display: none` for absent controls, and
`SteamInputGlyphStylePatch` installs it as `<style id="wsgm-handheld-glyphs">` in the main window
under an 8 s, 2 MiB bound. The patch is enabled only when the setting is on and the presentation has
something to show.

## 16. Configuration

`AppConfig.DeviceIntegration` (`Core\DeviceConfiguration.cs`):

| Key                           | Default     | Effect                                                                                                                  |
| ----------------------------- | ----------- | ----------------------------------------------------------------------------------------------------------------------- |
| `Enabled`                     | false       | Master switch. Off to on starts a fresh cycle; on to off stops with `IntegrationDisabled` and logs any unverified step. |
| `ControllerManagementEnabled` | false       | Child preference, remembered while the master is off; toggled live through the 6 s path.                                |
| `AutoTdpEnabled`              | false       | Runs only with the master on.                                                                                           |
| `GlyphSelection`              | `Automatic` | `Automatic`, `NativeSteam`, or a manual profile.                                                                        |
| `ManualGlyphProfileId`        | null        | The manual profile id.                                                                                                  |
| `OemAssignments[]`            | `[]`        | Allowlisted OEM control assignments.                                                                                    |
| `PluginSettings[]`            | `[]`        | Per plugin and device definition: stored values, cached declaration, authored profiles.                                 |

Loading repairs bad enum names so one bad value cannot quarantine the file. Normalization trims ids,
drops blank or duplicate entries, and drops invalid cached declarations and non-ascending curves.
Device values, the controller target and the fan-curve selection are profile values under
`AppConfig.Profiles`, keyed by the identity key (24 hex characters of SHA-256 over manufacturer,
baseboard product and version, SKU); see [profiles](profiles.md). Reload replaces the config object
and calls `ApplyConfigAsync`; coordinator-originated changes persist through `ConfigStore.Mutate`
under the transition gate.

## 17. Logging

`%LOCALAPPDATA%\WSGM\wsgm.log` is the remote-diagnosis surface. The lines that settle a device
question:

| Area          | Lines                                                                                                                                                                                                                                                                     |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Startup       | `Device plugin startup inventory: <cardinality>, roots=<n>.` and the refusal texts in §3                                                                                                                                                                                  |
| Maintenance   | every line prefixed `Device plugin maintenance:`                                                                                                                                                                                                                          |
| Cycle         | `Device cycle: state=…`, `Device cycle active: …`, `Device cycle passive: …`, `Device definition matched: …`, `Device plugin fault: …`, `Device plugin restart n/2 scheduled`, `Device cycle faulted after restart exhaustion`, `Device cycle <operation> was incomplete` |
| Controller    | `Controller management: state=…`, `Managed controller target created/kept/replaced: …`, `Controller released: …`, `Controller release: …`, `Controller forwarding paused for suspend`, `Controller forwarding resumed: …`, `Controller management disabled/enabled.`      |
| Capabilities  | `Device descriptor set rejected`, `Device capability available/unavailable`, `Device command: …`, `Late device command result reconciled/ignored`, `Desired-value reconciliation (…)`                                                                                     |
| Plugin traces | `plugin/<scope>: <message>`                                                                                                                                                                                                                                               |

`Log.Change` keys print once per transition: `device-plugin-publication-<channel>`,
`device-plugin-late-fault`, `device-capability-state-rejected/<key>`,
`device-capability-delta-rejected/<key>`, `managed-controller-neutralized`,
`controller-sample-after-dispose`, `controller-sample-route-fault`, `device-profile/<cap>`,
`device-command/<capability>`, `glyph.selection`, `steam.ui.glyphs`, `ui-capture.<surface>`.

A plugin reaches those same mechanics through the SDK: `PluginTrace.Debug` for detail the host
suppresses unless verbose logging is on, and `PluginTrace.Change(scope, key, message)` for anything
a poll loop observes. The host keys them `plugin/<scope>/<key>` so two subsystems cannot collide on
a short name, and applies its own repeat suppression. Before API 3 the plugin channel could not
reach `Log.Change` at all, which is why plugin lines were historically the worst repeaters in the
file. Levels and key style are in [logging](logging.md).

## 18. Worked example: the built-in MSI Claw package

`src\WSGM.Device.Msi.Claw` (MIT) is the reference plugin and the shape every rule above was tested
against. Its manifest is `wsgm.device.msi.claw`, API 10, entry `WSGM.Device.Msi.Claw.ClawPlugin`. It
targets `net10.0-windows10.0.19041.0`, references only the SDK and `System.Management`, ships its
licence and notices beside the assembly, declares no settings manifest, and keeps every vendor
address inside the package.

Identity: `DetectAsync` matches the SMBIOS baseboard manufacturer
`MICRO-STAR INTERNATIONAL CO., LTD.` and one of the five Claw baseboards in `ClawModels.cs`
(`MS-1T41`, `MS-1T42`, `MS-1T52`, `MS-1T8K`, `MS-1T91`), as Handheld Companion does, and returns
that model's definition id (`ms-1t52` for the reference unit). Only `MS-1T52` has hardware evidence.
Start re-reads identity and refuses a changed model. The WMI-backed services need only the MSI_ACPI
provider; the EC (or BIOS) version and the interface version bind the recovery journal but gate
nothing, and failing to read them changes nothing. The MCU revision (USB `bcdDevice`) is recorded
but never gated on; it only picks the lighting and paddle-mapping addresses from HC's firmware
table. Like HC, the plugin writes without readback: a matching read upgrades a result to verified, a
mismatch publishes the written value.

Transports: `MSI_ACPI` over WMI with 32-byte packages, a 3 s per-operation timeout and a required
status byte; the `MSI_Event` WMI event source for the front buttons; a HID vendor collection for the
MCU (profile read and write, mode switch with a 1 s acknowledgement and 50 ms topology polling); the
HID gamepad collection for DirectInput reports at about 125 Hz; the IMU through the SDK's legacy
Sensor API stream (`LegacyMotionStream`) with the gyrometer at a 10 ms report interval; Intel IGCL
through `ControlLib.dll` for Arc Sync; and a low-level keyboard hook that suppresses only the
captured firmware orphan G/Tab key-up flow, preserving complete keyboard chords. The shortcut policy
and software-only validation limits are in `device-integration.md`, "Claw OEM chord suppression".

Capabilities (one descriptor set per cycle, generation 1):

| Id                         | Instances                      | Role                  | Kind        | Bounds                           | R/W    | Persistence      | Section                                     |
| -------------------------- | ------------------------------ | --------------------- | ----------- | -------------------------------- | ------ | ---------------- | ------------------------------------------- |
| `power.primary-limit`      | –                              | `PowerSustainedLimit` | Integer W   | 8–37                             | R/W    | Volatile         | power / limits                              |
| `power.boost-limit`        | –                              | `PowerSlowLimit`      | Integer W   | 8–37                             | R/W    | Volatile         | power / limits                              |
| `battery.charge-limit`     | –                              | `ChargeLimit`         | Integer %   | 60–100                           | R/W    | DevicePersistent | power / charging                            |
| `power.scenario`           | –                              | `ScenarioMode`        | Choice      | comfort, green, eco, user, sport | R      | Volatile         | power                                       |
| `fan.mode`                 | –                              | `FanMode`             | Choice      | automatic, custom, full-speed    | R/W    | Volatile         | power / control                             |
| `fan.curve`                | –                              | `FanCurve`            | Curve %     | six points, 0–100                | R/W    | Volatile         | power / control                             |
| `fan.measured-rpm`         | left, right                    | `FanMeasuredRpm`      | Integer rpm | 0–10000                          | R      | Volatile         | info / readings                             |
| `telemetry.temperature`    | –                              | `Telemetry`           | Integer °C  | 0–110                            | R      | Volatile         | info / readings                             |
| `lighting.brightness`      | –                              | `LightingBrightness`  | Integer %   | 0–100                            | R/W    | DevicePersistent | lighting                                    |
| `lighting.zone-color`      | left-ring, right-ring, buttons | `LightingZoneColor`   | Color       | 24-bit                           | R/W    | DevicePersistent | lighting / zones                            |
| `controller.source`        | –                              | `ControllerSource`    | Choice      | device, plugin, unavailable      | R      | Volatile         | info / ownership                            |
| `motion.source`            | –                              | `MotionSource`        | Choice      | device, plugin, unavailable      | R      | Volatile         | info / ownership                            |
| `haptic.rumble`            | –                              | `HapticSink`          | action      | –                                | action | Volatile         | info / ownership                            |
| `display.variable-refresh` | –                              | `VariableRefreshRate` | Boolean     | –                                | R/W    | DevicePersistent | power, only when an Arc Sync panel answered |

The declared shared sections are `power` (icon Power; categories limits, charging, control titled
"Fans"), `rgb` (category zones) and `info` (icon Gauge; categories ownership, readings). Fan RPM is
`480000 / raw`. Every WMI write is bracketed by the recovery journal with a 2 s minimum write
budget, and "verified without readback" is normalized to `AppliedUnverified`.

**The fan curve is one capability, not a left and a right.** Both fans sit on one heatsink and the
firmware ramps them together, so two independently authored curves described a machine that does not
exist. `ApplyCurveAsync` writes both channels under ONE pre-write snapshot, so a failure on the
second restores the first; two `ApplyCurveAsync` calls could not, because the second call's snapshot
would already contain the first call's write. Only the six curve offsets are shared between the
channels; every other byte in each package is that channel's own and is preserved. The published
state is the left channel's table, which the pair can only disagree with if something outside WSGM
wrote one of them. The descriptor declares 0–100 bounds so the curve editor has a stated range to
draw and clamp against; an undeclared bound means "no limit" to the router.

**The readings are on Info, not Power.** Power is where a person goes mid-game to change how the
device behaves, and it used to end in a Thermals group of numbers to watch. The CPU temperature is
still published because the fan-curve editor marks it against the curve, and both fan speeds are
still measured separately because one failing fan is exactly the fault that page exists to show.
`info` also holds the three ownership rows that were a Controller page of their own, competing with
the page that has the actual controller settings on it.

Controller: acquisition requires management enabled, the exact machine identity (the MCU revision is
not a gate), the same composite USB location as first observed, a journaled switch to DirectInput
when needed, at least one physical device, then the source start and the physical-device publication
that starts WSGM's half. A controller that is not there yet leaves the service `Degraded` while the
SDK's `DeviceReconnect` checks every half second and takes it when it appears; a reader that stops
does the same, so the pad dropping out around a sleep is never a fault. The codec maps byte 5 bits
4–7 to X, A, B, Y; byte 6 to LB, RB, View, Menu, L3, R3; byte 7 bit 4 to `RearPaddle1` (M1) and bit
3 to `RearPaddle2` (M2), which is the opposite of Handheld Companion's reading; sticks are
`(v − 128) / 127` with Y negated. Front-button WMI events latch `Guide` and `QuickAccess` into the
sample for 200 ms through the SDK's `OemButtonLatch`. Rear-paddle edges also publish OEM events
`oem3` and `oem4` with press and release. On the MS-1T52 the package reads both physical LSM6DSO
collections through the SDK's `LegacyMotionSensors`: `Physical Gyrometer` in degrees/second and
`Physical Accelerometer` in g, both from custom fields 7/8/9 and both mapped from raw `(X, Y, Z)` to
application `(X, Z, -Y)`. The gyro's opaque `VT_UI4` field 34 distinguishes fresh hardware reports
from repeated results. The gyrometer asks for its 10 ms driver minimum and the accelerometer for the
gyrometer's interval; `LegacyMotionStream` takes reports by Sensor API event and falls back to a 2
ms poll where a sink cannot be registered, and the prior intervals are restored on release. Motion
runs with the cycle, stopping only for suspend and stop; there is no demand signal. This part's gyro
carries a real zero-rate offset that Intel ISS does not remove and no controller target corrects, so
the SDK's `MotionSampleBuilder` runs `StationaryGyroBiasCalibrator`, which measures it from
200-report rest windows, gated on rate span, acceleration span and gravity magnitude, and subtracts
it. Subtraction only: a deadband or a zero-hold would replace the drift with a dead zone around
rest. Readings older than 50 ms stop contributing angular velocity and the frame average
(`GyroFrameResampler`) preserves their area. Motion writes no per-report file and emits no
per-sample line into `wsgm.log`; only the measured offset and read failure transitions are logged.
No acceleration or orientation is synthesized.

Haptics: low and high frequency native, triggers unsupported, 250 frames per second,
`MinimumStartIntensity = 56/255` and `MinimumPulse = 10 ms` (Claw sweep, 2026-09-02). Output report
`0x05 0x01 … weak strong`; the host paces output to the declared frame rate, identical values are
not rewritten, and release writes zero before stopping the reader.

OEM controls: `oem1` "Claw button" and `oem2` "Quick Settings" are front controls from WMI codes
`0x29`, `0x58` (short) and `0x2A` (long); `oem3` M1 and `oem4` M2 are rear controls requiring
acquisition.

Recovery: `temporary-state.v1.json` in the host-supplied state directory, 16 KiB, at most three
entries for `msi-power`, `msi-fans` and `physical-controller`, written atomically. On start the
plugin restores an entry whose firmware identity matches, blocks the service after a failed restore,
and otherwise reports only.

Glyphs: one profile `msi-claw` for all five definition ids, 23 named assets (20 control SVGs at
32×32, one full-controller SVG, left and right PNGs at 643×464), 20 control mappings with the
printed labels, no aliases, notice `THIRD_PARTY_NOTICES.md`.

Tests build the plugin with fake WMI, MCU, controller, motion, chord and event services and the
SDK's `TestPluginHostAdapter`. Packaging:
`eng\pack-device.ps1 -Source src\WSGM.Device.Msi.Claw -RequireGlyphs` publishes framework-dependent
`win-x64`, strips symbols, copies `glyphs\` verbatim and requires a profile, runs
`wsgm-device validate` and `wsgm-device pack`. WSGM's `eng\build-bundle.ps1` publishes Device Lab,
invokes that packer, checks the archive's path safety, extracts a copy, requires the licence,
notices and provenance files, compares the glyph count with the source tree, validates again, and
stages the archive itself as `Packages\<id>-<version>.wsgmpkg`, which the setup carries and installs
when the hardware matches. `eng\dev-deploy.ps1` drops a fresh build into the Plugins folder.

## 19. Device Lab

`wsgm-device` (`src\WSGM.DeviceLab`, MIT) is the authoring and diagnostic tool. No argument opens
the GUI; every command prints camelCase JSON to stdout, diagnostics to stderr, and exits 0, 64
(usage) or 70 (failure). Unknown options are rejected up front.

| Command                                                                             | Arguments                                                                                                                                   | Class              |
| ----------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- | ------------------ |
| `doctor --out-dir <dir>`                                                            | environment, API exports, elevation, output policy                                                                                          | read-only          |
| `inventory --out-dir <dir> [--shareable]`                                           | firmware, USB, WMI presence, sensors, processes; `--shareable` redacts                                                                      | read-only          |
| `candidates --from <inventory.json> [--device-id]`                                  | known-device matching                                                                                                                       | offline            |
| `probe-read --from <inventory.json> [--run <id> --out-dir <dir>]`                   | compiled MSI read probes after an exact match, elevation and owner absence                                                                  | read-only hardware |
| `capture run --recipe <recipe.json> --out-dir <dir>`                                | observe-only capture; export requires typing `OBSERVE` then `EXPORT`                                                                        | attended           |
| `inspect <cap>`, `compare <a> <b>`, `correlate <cap> --action <id> --sources <a,b>` | capture analysis                                                                                                                            | offline            |
| `fixture extract --from <cap> --id <id> --out-dir <dir>`                            | test fixture from a capture                                                                                                                 | offline            |
| `scaffold --from <cap> --out-dir <dir> [--usb-instance <id>]`                       | manifest, project, plugin skeleton, README, licence                                                                                         | offline            |
| `glyph import <package-dir>`                                                        | SDK importer report                                                                                                                         | offline            |
| `validate <package-dir>`                                                            | manifest, layout, x64 entry, budgets; never loads code                                                                                      | offline            |
| `test sample`                                                                       | the built-in synthetic fixture                                                                                                              | offline            |
| `test plugin <dir> --from <inventory.json>`                                         | loads the package in a worker and runs `DetectAsync` only                                                                                   | loads code         |
| `test hardware <dir> --from <inventory.json> --state-dir <new> --action …`          | one attended action: `capability --capability <id> [--instance <id>] --value <v>`, `haptic`, `haptic-sweep`, `controller`; `--yes` rejected | attended           |
| `pack <package-dir> --out <new.wsgmpkg>`                                            | deterministic archive from pinned handles                                                                                                   | offline            |

Read-only is the default, and every output path is explicit. Capture, manifest, package and request
parsers accept external files, so they bound sizes and shapes; shareable capture output is redacted;
the tool never reads or writes live `%LOCALAPPDATA%\WSGM` state; and offline commands do not load
plugin code. The one mutation path is the attended action below. It has no `--yes`, bulk, CI,
remembered-consent or imported-operation route.

The attended path validates offline, requires a new state directory that passes the output-path
policy (no drive roots, profile folders, repository root or live WSGM data), an interactive
terminal, no CI, elevation and the typed confirmation `RUN HARDWARE`. It then atomically reserves
`Global\WSGM.DeviceOwner`, loads the plugin, requires `DetectAsync` to match, starts with controller
management off, runs the one action, collects diagnostics and always stops with
`IntegrationDisabled`. Each lifecycle phase has a 15 s budget; the haptic sweep has five minutes. If
start was attempted and cleanup was not clean, the owner reservation is retained until the process
exits.

A `.wsgmcap` is a ZIP with `manifest.json`, `recipe.json`, `inventory.json`, `redaction.json` and
`hashes.sha256` at its root, streams as NDJSON, and is bounded to 4096 entries, 256 MiB
uncompressed, 64 MiB per blob, 1 MiB per event and 128 sources.

## 20. Verification boundary

Automated tests cover package cardinality and containment, the stager transaction and its crash
points, gate exclusion and abandoned-mutex recovery, lifecycle ordering and cancellation, stale
generation rejection, teardown ordering under throwing subscribers, router validation and freshness,
profile selection and validation, overlay projection, OEM policy, glyph selection and CSS, the
controller release order, and the Claw plugin against its fakes. They use temporary directories and
the existing injected seams and never touch `%LOCALAPPDATA%\WSGM`.

Hardware writes, controller mode switches, HidHide changes, live Steam glyph patching and the
attended Device Lab actions remain device verification on the reference Claw and must record the
exact build, device, observed result and cleanup.

## 21. Known gaps

- OEM assignments have no authoring UI, and will not get one (§13).
- `eng\dev-deploy.ps1` swaps inside `installed` without the machine-wide objects (§6).
- Unload of the plugin context is requested, never verified (§7).
