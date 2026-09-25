# Device plugin system

This is the mechanism reference for how WSGM hosts a device plugin: the package on disk, how it is
discovered, protected, installed and loaded, the lifecycle WSGM drives it through, what happens to
each publication, how commands travel back to hardware, and how the controller, haptic, OEM,
settings, profile and glyph paths are wired. File names are given where a reader has to go to the
code. The reasons behind these mechanisms and the device findings are in `device-integration.md`.

Read it together with:

| Document                                    | Holds                                                                |
| ------------------------------------------- | -------------------------------------------------------------------- |
| `src\WSGM.Device.Sdk\docs\reference.md`     | Every SDK type, rule and limit: the contract a plugin links against. |
| `docs\device-integration.md`                | The decisions and device findings behind the runtime.                |
| `docs\device-plugin-authoring.md`           | The author workflow: scaffold, build, test, pack, install.           |
| `docs\device-security.md`                   | The one-page boundary checklist.                                     |
| `_plan\2.0-decisions.md` D02–D10, D20, D22b | The standing product decisions.                                      |

## 1. Components and ownership

The Shell's common `PluginHost` reserves the Device category and drives its
`DevicePluginCompatibilityAdapter`. The coordinator retains the device-specific make-safe ordering
shown below; the adapter delegates to the existing runtime. See `plugin-system.md` for common
instance deadlines, retained failed slots and generation-checked health.

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
  │   │    ├─ HidHideOwnedDeltaManager (Shell\HidHideOwnership.cs)                        │
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
| `apiVersion` equals `DeviceApi.Version` (6)                               | `api-incompatible`         |
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

| State                 | Entered when                                                                                                                                                          |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Disabled`            | Integration off, or a cycle ended intentionally.                                                                                                                      |
| `Detected`            | A cycle start began; the Plugins folder is about to be read.                                                                                                          |
| `Passive`             | No valid package, or the plugin did not match the machine.                                                                                                            |
| `Activating`          | The runtime is loading, or a restart is scheduled.                                                                                                                    |
| `Active` / `Degraded` | The plugin's `PluginStartResult`.                                                                                                                                     |
| `Suspended`           | After a successful suspend.                                                                                                                                           |
| `Deactivating`        | During an intentional stop.                                                                                                                                           |
| `Faulted`             | Restart attempts exhausted, fault cleanup unverified, or a restart failed. Fails open: the virtual target and WSGM's HidHide entries are gone; desired state is kept. |

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
coordinator tears the client down with a 15 s deadline (make-safe, `StopAsync(RuntimeFault)`,
detach, dispose), and then:

- an unverified teardown sets `Faulted` and blocks restart ("fault cleanup was incomplete");
- an intentional stop, disposal, or integration off sets `Disabled`;
- otherwise it logs `Device plugin fault: generation=…, reason=…, detail=…` and schedules a restart.

Restarts are bounded to two attempts with backoffs of 1 s and 4 s
(`Device plugin restart n/2 scheduled in x s.`). Exhaustion sets `Faulted` and logs
`Device cycle faulted after restart exhaustion`. Manual retry from the overlay's recovery row works
only from `Faulted` and is refused while prior hardware cleanup was unverified.

### Suspend and resume

Session lock and system suspend trigger suspend; unlock and resume trigger resume. The system events
reach the shell's message-only window only because `MessageWindow` registers for them with
`RegisterSuspendResumeNotification`; Windows broadcasts the suspend and resume codes to top-level
windows alone, and until 2026-09-22 nothing was registered, so no sleep ever suspended or resumed
the cycle and the Claw's lighting came back from standby at the firmware default. The shell
edge-triggers and serializes them, so overlapping lock and sleep events collapse. Suspend: block
forwarding, make the controller safe with `ControllerOnly` scope, `client.SuspendAsync`, reset the
OEM router. Resume: re-collect identity, advance the cycle generation, `client.ResumeAsync` (the
runtime requires `Suspended` and a strictly greater generation), then synchronize the generation
into the router and OEM router.

### Stop and shutdown

Turning integration off stops with reason `IntegrationDisabled`; shutdown maps the application
reason to `Updating`, `SessionEnding`, `Uninstalling` or `WsgmExiting`. The order is fixed and every
step's failure is retained while cleanup continues: close command admission, state `Deactivating`,
controller make-safe with `FullDeactivation`, `client.StopAsync`, detach, dispose, state `Disabled`.
A verified teardown means the handoff reached `TopologyVerified` or `WsgmStateRemoved` with
`ReleasedVerified` and the stop reported `Clean`; anything else surfaces as "Device hardware
teardown completed, but one or more release steps were unverified." The shell logs it and continues.

Shutdown cancels the coordinator's lifetime before waiting for the transition gate, so an in-flight
start unwinds under the shutdown owner's deadline rather than stacking a second budget.

### Controller management toggled inside a cycle

Disable: make-safe with `ControllerOnly`, then `SetControllerManagementAsync(false)`; if the plugin
does not acknowledge, the cycle is stopped as `RuntimeFault` and restarted with the persisted
policy. Enable: allowlist WSGM in HidHide, a fresh generation, `SetControllerManagementAsync(true)`,
then generation synchronization.

### Deadlines

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
| Controller sample                      | Current cycle generation.                                                                                      | `ControllerManager.Submit`: one-slot latest-wins pump (§12).                                                                                                                                                                                                                                                                                                    |
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
`Deadline = now + timeout`, then refuses with `Rejected` in this order:

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
to the profile layer in force (`docs\profiles.md`).

Debouncing lives in the controls, not the router: the slider commits 250 ms after the last change,
and the colour editor writes only on Apply (colour, then brightness).

## 11. Desired state, settings and profiles

### Desired values

A capability's desired value is its profile value: the running game's enabled profile, then Global,
then none. The model and every rule about it are in `docs\profiles.md`. The values live in
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
refused command may be retried, an uncertain one only after a readback newer than its result shows
the zone does not hold the value. Pending commands block automatic restoration. The saved value
remains intact after every failure. Diagnostics report
`Device restore <capability>/<instance> from <layer> (<reason>): outcome=…, attempt n of 3.` and the
`Desired-value reconciliation (…)` summary. A manual command suppresses readiness restoration while
its hardware result and desired configuration are being committed.

During resume and controller reacquisition, the capability router accepts the attached runtime's new
cycle before validating its first descriptor publication. Descriptor numbering can restart within
that cycle. The coordinator's later synchronization preserves the freshly accepted state.

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
WSGM's HidHide delta, UI capture, the UI input source and the make-safe handoff. Its states are
`Off`, `Unavailable`, `Idle`, `Active` and `Faulted`; WSGM's own surfaces read from the managed
canonical source only while `Active` and from SDL plus the Steam Input lease otherwise.

### Start

Management starts when the plugin publishes physical devices, not at cycle start:

1. Store the devices, selection and generation; return `Off` when management is disabled.
2. `ViiperControllerBackend.DiscoverAsync` must report ready with capabilities, else `Unavailable`.
3. Resolve the target from the `ControllerTarget` profile value for the running application
   (`SteamDeckComposite` when no layer sets one).
4. `HidHideOwnedDeltaManager.StartAsync` allowlists WSGM and hides every identity marked
   `RequiresHiding`; not activated means `Unavailable`.
5. Create the target (or replace the old one) and activate the source; failure cleans HidHide and
   sets `Faulted`. Success logs `Controller management: state=Active, target=…, source=…`.

Every unavailable prerequisite fails open: the shell, SDL input and the Steam Input lease continue
unchanged.

### Samples

`Submit` drops a sample after disposal or with a stale generation (`Log.Change` keys
`controller-sample-after-dispose`, `controller-stale-sample`), then overwrites the single pending
slot and starts one drain loop if none is running. Newer samples replace unread ones; nothing
queues. Routing raises the unfiltered diagnostic event, then sends the sample to the UI (captured,
forwarding blocked, or not yet resumable) or to the target. Before the target sees it,
`ManagedControllerSampleValidator` requires the same generation, a strictly increasing sequence, a
timestamp within ±1 s, `Quality == Good`, sticks within −1…1, triggers within 0…1 and finite motion;
a failure neutralizes the target and logs a warning.

### Targets and encoders

VIIPER binds `libviiper`, listens on `127.0.0.1:0`, bus 1, and creates a target as add, open, submit
a neutral frame, register the feedback callback, attach. The Steam Deck target sends the 64-byte
Neptune state: buttons at bytes 8–14, pads 16–23, motion 24–35 with accelerometer counts of 16384
per g and gyro counts of 16 per degree per second on the `X, -Z, Y` axes, triggers scaled to 32767,
sticks clamped to the signed range, forces at 56–63. Xbox 360 maps the standard buttons, byte
triggers and signed sticks; DualShock 4 additionally maps touch contacts, gyro and acceleration. The
target is replaced as one neutralize, remove, create operation, and the usbip-win2 client attachment
is plugged out by port before the server device is deleted.

### Haptic return path

VIIPER calls the feedback callback on a library thread. For the Deck target:

| Report | Meaning                                                                                              |
| ------ | ---------------------------------------------------------------------------------------------------- |
| `0xEB` | rumble: two 16-bit values over 65535                                                                 |
| `0xDC` | haptic event: byte 3 (0 stop, 1 half, else full) with a 150 ms stop                                  |
| `0xEA` | trackpad haptics: stop after 35 ms                                                                   |
| `0x8F` | pulse: `min(255, count·16 + report[9]) / 255`, stopping after `period·count` ms clamped to 1…5000 ms |
| `0xE2` | gain: ignored                                                                                        |

Unknown command ids are logged at most four times each. `ControllerOutputRouter` admits into a
channel of capacity 1 that drops the oldest, requiring a matching target generation and kind, a
timestamp no more than 1 s ahead, an age of at most 250 ms, finite channels and a positive stop
time. The run loop drops frames whose sink generation or ownership changed, clamps unsupported
channels, floors bounded events (not continuous rumble) to the plugin's `MinimumStartIntensity` and
stretches their stop to at least `MinimumPulse`, paces at `1 / MaxFramesPerSecond` clamped to 1…1000
fps, applies through `PluginHapticSink`, and schedules the pulse stop. The sink admits frames only
while owned by the current generation, sends an explicit stop frame because the physical motors
latch, and waits for in-flight frames before detachment. The runtime forwards to
`IDevicePlugin.ApplyHapticOutputAsync` only in `Active` or `Degraded`. The first physical output
admitted for each target is logged once, never at report cadence.

### UI capture

`UiCaptureState` holds a set of surface ids. The first claim snapshots the controls held at open as
both "suppressed for the UI" and "withheld from the game"; a duplicate claim is logged and refused.
While captured, samples reach only the UI with the suppressed controls masked until physically
released. After the last release, forwarding resumes only on the first sample in which every
withheld control is up. The overlay's rear-button OEM action pulses `RearPaddle1` or `RearPaddle2`
for 80 ms and always publishes the release.

### Make-safe handoff

`MakeSafeUnderGateAsync` records each step in the SDK's `ControllerHandoffStep` vocabulary:

1. Clear the pending sample; block forwarding; neutralize the target (`VirtualTargetNeutralized`).
2. Ask the plugin to release (`ReleaseControllerAsync`); it must report a plugin-owned step from
   `PhysicalAcquisitionStopped` to `TopologyUnverified`; an exception records the release as
   unobserved.
3. Remove the target, guarded by "the physical release has concluded either way".
4. Remove WSGM's HidHide entries, guarded by "the target is gone".
5. `ReleasedVerified` only when every step verified, else `ReleasedUnverified`; state `Off` for
   `FullDeactivation`, `Idle` for `ControllerOnly`; one log line
   `Controller make-safe: scope=…, step=…, result=…, targetRemoved=…, hidHideRemoved=…`.

### HidHide ledger

`HidHideOwnedDeltaManager` keeps its deltas in `%LOCALAPPDATA%\WSGM\hidhide-ownership.json`. It
recovers an orphaned ledger from a previous run before starting, refuses when HidHide is not ready
or is in inverse mode, records each delta as `Pending` then `Applied` around a compare-and-swap with
at most three retries, verifies by readback, reverses newest-first, refuses ambiguous entries, and
deletes the ledger only when everything was removed. Paths compare equal across DOS and NT device
notation; the findings behind that and the pre-start allowlist are in `device-integration.md`,
"HidHide findings".

## 13. OEM controls

`DeviceOemActionRouter` maps a published control's press to one WSGM action from the closed
`OemAction` vocabulary stored under `DeviceIntegration.OemAssignments`:

| Action                                                    | Effect                                                                    |
| --------------------------------------------------------- | ------------------------------------------------------------------------- |
| `ToggleWsgmOverlay`                                       | Toggle the overlay.                                                       |
| `ToggleSteamQuickAccess`                                  | Replay Steam's native Quick Access button through the controller handoff. |
| `ToggleSteamOverlay`                                      | Replay Steam's native Home/Overlay button through the same handoff.       |
| `ShowWsgmDevicePage`                                      | Open the overlay's Device page.                                           |
| `ToggleWsgmTaskbar`                                       | Toggle the Open apps strip.                                               |
| `ToggleDesktopGameMode`                                   | Enter Game Mode if Explorer runs, else Desktop Mode.                      |
| `ToggleOnScreenKeyboard`                                  | Toggle the touch keyboard.                                                |
| `CyclePerformanceProfile`, `CyclePerformanceOverlayLevel` | RTSS cycles.                                                              |
| `VirtualTargetRearButton1`, `VirtualTargetRearButton2`    | Pulse a rear paddle on the target.                                        |

An unassigned control resolves to `Disabled`: WSGM claims no physical button by default, and the
plugin exposes the front buttons to Steam as the target's own Guide and Quick Access buttons.
Assignments are authored in plugin code, and there is no UI to rebind them because WSGM does not
build a remapper: every handheld on the market today maps cleanly onto a Steam Deck controller with
no buttons or functions left over, so a remapper would be a general-purpose feature answering a
problem no supported device has. Rear-button actions are assignable only to `Rear` placement and
only when the target has rear buttons (Steam Deck); a control that `RequiresControllerAcquisition`
needs management enabled.

Events are refused for a stale source generation, an unknown control, a blank or over-long (128)
deduplication id, a timestamp more than 5 s in the future, or one older than the 30 s deduplication
window. Release edges are logged and ignored: actions run on press only. Duplicates within the
window are suppressed through a 256-entry table. Each action runs under a 3 s budget and logs
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
maps each WSGM section to the `SettingSectionKey` that means the same thing — `Power`, `Controller`,
`Lighting`, `Diagnostics`, `General` — and a declared section carrying that key absorbs it:
`AbsorbedBy` folds its count and status into the declared card, and `RenderOwnedDeviceRows` draws
its rows on the declared page after the device's own. Without this a device declaring a Power
section produced that page **and** WSGM's, with the power limits on one and the frame limit on the
other; the same split gave two Controller pages. `Oem` deliberately maps to nothing: it is WSGM
policy over a plugin's controls, and no plugin has a vocabulary for that subject. The shared
performance rows follow the absorption too, so they stay on whichever page power ended up being.

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
Up/Down moves it, on pad and keyboard alike. Two things differ from HC, both because they are device
facts rather than design choices: the nodes sit at the breakpoints the firmware actually stores (six
on the Claw, not HC's fixed eleven), and their inputs are pinned while outputs move, because those
breakpoints are the fan table. `RisingOutput` holds each output between its neighbours' — the fan
firmware refuses a table whose duties dip, and a drag that would build one has to be impossible
rather than reported on apply. The three presets are HandheldCompanion's own `IDevice.fanPresets`
arrays (Quiet, Default, Aggressive), stored at HC's 11-point resolution in `Core\FanCurvePresets.cs`
and interpolated onto the device's own temperatures at apply time, so a preset never invents a
breakpoint the table does not have.

A row's value is the pending value, else the desired value, else the observed value. Its status
follows the projection: `Progress` while pending; `Faulted` on failure or `TransportFaulted`;
`Warning` for uncertain or out-of-range; `Stale` for expired or generation-changed;
`ExternallyOwned` for `ResourceConflict` or `ResourceReleased`; `Unsupported` for `Unsupported`,
`FirmwareNotVerified` or `PrerequisiteMissing`. An available action-only capability with no readback
is `Ready` and runnable rather than `Unknown`. A refresh is skipped while a control has focus so
telemetry cannot destroy an edit. The authored-profile row states scope: "applies to this game only"
or "applies to everything".

Settings owns the master toggle, controller management, AutoTDP, the managed target, glyph
selection, the plugin's declared settings and profile authoring. It never becomes a device control
surface (D22b). The standalone Settings process reads the coordinator's diagnostics snapshot (state,
package id and version, cycle generation, capability counts) over the named pipe
`WSGM.DeviceCoordinator.<sessionId>` with a 750 ms timeout. The native QAM and AutoTDP consume the
same router: AutoTDP takes the first writable integer `PowerSustainedLimit`, ticks every second, and
never retries an uncertain write; the QAM's TDP control requires a watt-unit descriptor with
`1 ≤ min < max ≤ 200`.

AutoTDP additionally requires a verified active frame-rate limit. One service availability result
guards enable commands and disables both UI controls with the same reason. Limiter-off events
relinquish runtime control and clear the enabled setting; see `rtss.md` for the ownership contract.

`PairedPowerLimitId` opts a sustained descriptor into plugin-owned paired commands. AutoTDP sends
`ApplyPowerPair` with captured cycle/descriptor generations and requires verified results. Both
original limits are retained for release; readback after an uncertain result must be newer than that
result before automatic control can continue. The Claw maps the target to equal PL1/PL2 values
through its existing ordered-write and rollback implementation. Other plugins may publish different
companion bounds and steps. The sustained descriptor's range defines coordinated targets; the plugin
owns the mapping and confirms both limits. Host validation does not impose the Claw's equal-limit
policy on other hardware.

The manual TDP preferences are four profile values: `TdpUnified`, `UnifiedWatts`, `SustainedWatts`
and `BoostWatts`. Each resolves on its own, so a game that sets one inherits the rest from Global.
These values are preferences rather than readback. Saved unified targets restore through the paired
command with captured generations and verified readback; profile-owned pair release also uses the
coordinated path. Both surfaces expose the shared mode and retain readback. Split restoration
validates the saved boost against its descriptor, applies the plugin's coordinated target and then
restores the independent boost under one power-mutation gate. Both results must be verified; there
is no retry after uncertainty. Manual sustained edits from Overlay and QAM now consult the same
active profile in the coordinator to select paired dispatch. Verified independent boost edits save
the advanced boost value and select split mode while retaining unified history. Manual sustained
edits use the active mode to select paired dispatch. Saving a unified target preserves the stored
advanced values, and saving an advanced sustained value preserves the unified target. Overlay Device
now exposes an Advanced/split versus Unified selector. Selection persists only policy; a subsequent
sustained-slider edit applies the coordinated target. QAM exposes the same mode toggle and one TDP
slider in unified mode.

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

| Key                           | Default     | Effect                                                                                                              |
| ----------------------------- | ----------- | ------------------------------------------------------------------------------------------------------------------- |
| `Enabled`                     | false       | Master switch. Off to on starts a fresh cycle; on to off stops with `IntegrationDisabled` and must verify teardown. |
| `ControllerManagementEnabled` | false       | Child preference, remembered while the master is off; toggled live through the 6 s path.                            |
| `AutoTdpEnabled`              | false       | Runs only with the master on.                                                                                       |
| `GlyphSelection`              | `Automatic` | `Automatic`, `NativeSteam`, or a manual profile.                                                                    |
| `ManualGlyphProfileId`        | null        | The manual profile id.                                                                                              |
| `OemAssignments[]`            | `[]`        | Allowlisted OEM control assignments.                                                                                |
| `PluginSettings[]`            | `[]`        | Per plugin and device definition: stored values, cached declaration, authored profiles.                             |

Loading repairs bad enum names so one bad value cannot quarantine the file. Normalization trims ids,
drops blank or duplicate entries, and drops invalid cached declarations and non-ascending curves.
Device values, the controller target and the fan-curve selection are profile values under
`AppConfig.Profiles`, keyed by the identity key (24 hex characters of SHA-256 over manufacturer,
baseboard product and version, SKU); see `docs\profiles.md`. Reload replaces the config object and
calls `ApplyConfigAsync`; coordinator-originated changes persist through `ConfigStore.Mutate` under
the transition gate.

## 17. Logging

`%LOCALAPPDATA%\WSGM\wsgm.log` is the remote-diagnosis surface. The lines that settle a device
question:

| Area          | Lines                                                                                                                                                                                                                                                                     |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Startup       | `Device plugin startup inventory: <cardinality>, roots=<n>.` and the refusal texts in §3                                                                                                                                                                                  |
| Maintenance   | every line prefixed `Device plugin maintenance:`                                                                                                                                                                                                                          |
| Cycle         | `Device cycle: state=…`, `Device cycle active: …`, `Device cycle passive: …`, `Device definition matched: …`, `Device plugin fault: …`, `Device plugin restart n/2 scheduled`, `Device cycle faulted after restart exhaustion`, `Device cycle <operation> was incomplete` |
| Controller    | `Controller management: state=…`, `Controller make-safe: …`, `Controller suspend handoff: …`, `Controller management disabled/enabled: …`                                                                                                                                 |
| Capabilities  | `Device descriptor set rejected`, `Device capability available/unavailable`, `Device command: …`, `Late device command result reconciled/ignored`, `Desired-value reconciliation (…)`                                                                                     |
| Plugin traces | `plugin/<scope>: <message>`                                                                                                                                                                                                                                               |

`Log.Change` keys print once per transition: `device-plugin-publication-<channel>`,
`device-plugin-late-fault`, `device-capability-state-rejected/<key>`,
`device-capability-delta-rejected/<key>`, `controller-stale-sample`,
`controller-sample-after-dispose`, `controller-sample-route-fault`, `device-profile/<cap>`,
`device-command/<capability>`, `glyph.selection`, `steam.ui.glyphs`, `ui-capture.<surface>`.

A plugin reaches those same mechanics through the SDK: `PluginTrace.Debug` for detail the host
suppresses unless verbose logging is on, and `PluginTrace.Change(scope, key, message)` for anything
a poll loop observes. The host keys them `plugin/<scope>/<key>` so two subsystems cannot collide on
a short name, and applies its own repeat suppression. Before API 3 the plugin channel could not
reach `Log.Change` at all, which is why plugin lines were historically the worst repeaters in the
file. Levels and key style are in `docs\logging.md`.

## 18. Worked example: the built-in MSI Claw package

`src\WSGM.Device.Msi.Claw8A2Vm` (MIT) is the reference plugin and the shape every rule above was
tested against. Its manifest is `wsgm.device.msi.claw-8-a2vm`, API 6, entry
`WSGM.Device.Msi.Claw8A2Vm.Claw8A2VmPlugin`. It targets `net10.0-windows10.0.19041.0`, references
only the SDK and `System.Management`, ships its licence and notices beside the assembly, declares no
settings manifest, and keeps every vendor address inside the package.

Identity: `DetectAsync` matches SMBIOS manufacturer `MICRO-STAR INTERNATIONAL CO., LTD.`, baseboard
`MS-1T52` and SKU `1T52.1` and returns definition id `ms-1t52`. Start re-reads identity and gates
the WMI-backed services on the EC firmware (`Get_EC` prefix `1T52EMS1.109`); a mismatch leaves those
services unavailable with `FirmwareNotVerified`. The MCU revision (USB `bcdDevice`) is recorded in
the identity snapshot but never gated on: controller ownership needs only the exact machine, and
lighting verifies the committed RGB profile's shape at `0x024A` on every acquire and goes passive,
not faulted, when a controller firmware changes it.

Transports: `MSI_ACPI` over WMI with 32-byte packages, a 3 s per-operation timeout and a required
status byte; the `MSI_Event` WMI event source for the front buttons; a HID vendor collection for the
MCU (profile read and write, mode switch with a 1 s acknowledgement and 50 ms topology polling); the
HID gamepad collection for DirectInput reports at about 125 Hz; the WinRT gyrometer at a 10 ms
report interval; Intel IGCL through `ControlLib.dll` for Arc Sync; and a low-level keyboard hook
that intercepts Win+G key-down and the firmware's orphan key-up chords. The shortcut policy and
software-only validation limits are in `device-integration.md`, "Claw OEM chord suppression".

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
channels — every other byte in each package is that channel's own and is preserved. The published
state is the left channel's table, which the pair can only disagree with if something outside WSGM
wrote one of them. The descriptor declares 0–100 bounds so the curve editor has a stated range to
draw and clamp against; an undeclared bound means "no limit" to the router.

**The readings are on Info, not Power.** Power is where a person goes mid-game to change how the
device behaves, and it used to end in a Thermals group of numbers to watch. The CPU temperature is
still published because the fan-curve editor marks it against the curve, and both fan speeds are
still measured separately because one failing fan is exactly the fault that page exists to show.
`info` also holds the three ownership rows that were a Controller page of their own, competing with
the page that has the actual controller settings on it.

Controller: acquisition requires management enabled, the identity and MCU gates, the same composite
USB location as first observed, a journaled switch to DirectInput when needed, at least one physical
device, then the source start and the physical-device publication that starts WSGM's half. The codec
maps byte 5 bits 4–7 to X, A, B, Y; byte 6 to LB, RB, View, Menu, L3, R3; byte 7 bit 4 to
`RearPaddle1` (M1) and bit 3 to `RearPaddle2` (M2), which is the opposite of Handheld Companion's
reading; sticks are `(v − 128) / 127` with Y negated. Front-button WMI events latch `Guide` and
`QuickAccess` into the sample for 120 ms. Rear-paddle edges also publish OEM events `oem3` and
`oem4` with press and release. The package reads both physical LSM6DSO collections through legacy
`sensorsapi`: `Physical Gyrometer` in degrees/second and `Physical Accelerometer` in g, both from
custom fields 7/8/9 and both mapped from raw `(X, Y, Z)` to application `(X, Z, -Y)`. The gyro's
opaque `VT_UI4` field 34 distinguishes fresh hardware reports from repeated polling results. The
cycle requests the gyro's 10 ms and accelerometer's 2 ms driver minima, polls every 2 ms, and checks
the gyro counter before reading the accelerometer, then restores the prior intervals on release when
still owned. This part's gyro carries a real zero-rate offset that Intel ISS does not remove and no
controller target corrects, so `StationaryGyroBiasCalibrator` measures it from 200-report rest
windows — gated on rate span, acceleration span and gravity magnitude — and subtracts it.
Subtraction only: a deadband or a zero-hold would replace the drift with a dead zone around rest.
Readings older than 50 ms stop contributing angular velocity and the frame average preserves their
area. Motion writes no per-report file and emits no per-sample line into `wsgm.log`; only the
measured offset and read failure transitions are logged. No acceleration or orientation is
synthesized.

Haptics: low and high frequency native, triggers unsupported, 250 frames per second,
`MinimumStartIntensity = 56/255` and `MinimumPulse = 10 ms` (Claw sweep, 2026-09-02). Output report
`0x05 0x01 … weak strong`; identical values are not rewritten, non-zero writes are gated to one per
4 ms, and release writes zero before stopping the reader.

OEM controls: `oem1` "Claw button" and `oem2` "Quick Settings" are front controls from WMI codes
`0x29`, `0x58` (short) and `0x2A` (long); `oem3` M1 and `oem4` M2 are rear controls requiring
acquisition.

Recovery: `temporary-state.v1.json` in the host-supplied state directory, 16 KiB, at most three
entries for `msi-power`, `msi-fans` and `physical-controller`, written atomically. On start the
plugin restores an entry whose firmware identity matches, blocks the service after a failed restore,
and otherwise reports only.

Glyphs: one profile `claw-8-a2vm` for `ms-1t52`, 23 named assets (20 control SVGs at 32×32, one
full-controller SVG, left and right PNGs at 643×464), 20 control mappings with the printed labels,
no aliases, notice `THIRD_PARTY_NOTICES.md`.

Tests build the plugin with fake WMI, MCU, controller, motion, chord and event services and the
SDK's `TestPluginHostAdapter`. Packaging:
`eng\pack-device.ps1 -Source src\WSGM.Device.Msi.Claw8A2Vm -RequireGlyphs` publishes
framework-dependent `win-x64`, strips symbols, copies `glyphs\` verbatim and requires a profile,
runs `wsgm-device validate` and `wsgm-device pack`. WSGM's `eng\build-bundle.ps1` publishes Device
Lab, invokes that packer, checks the archive's path safety, extracts a copy, requires the licence,
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
make-safe sequence rules, and the Claw plugin against its fakes. They use temporary directories and
the existing injected seams and never touch `%LOCALAPPDATA%\WSGM`.

Hardware writes, controller mode switches, HidHide changes, live Steam glyph patching and the
attended Device Lab actions remain device verification on the reference Claw and must record the
exact build, device, observed result and cleanup.

## 21. Known gaps

- OEM assignments have no authoring UI, and will not get one (§13).
- `eng\dev-deploy.ps1` swaps inside `installed` without the machine-wide objects (§6).
- Unload of the plugin context is requested, never verified (§7).
