# Device plugin system

This is the mechanism reference for how WSGM hosts a device plugin: the package on disk, how it is
discovered, protected, installed and loaded, the lifecycle WSGM drives it through, what happens to
each publication, how commands travel back to hardware, and how the controller, haptic, OEM,
settings, profile and glyph paths are wired. File names are given where a reader has to go to the
code. The reasons behind these mechanisms and the device findings are in `device-integration.md`.

Read it together with:

| Document                                     | Holds                                                                |
| -------------------------------------------- | -------------------------------------------------------------------- |
| `external\WSGM.Device.Sdk\docs\reference.md` | Every SDK type, rule and limit: the contract a plugin links against. |
| `docs\device-integration.md`                 | The decisions and device findings behind the runtime.                |
| `docs\device-plugin-authoring.md`            | The author workflow: scaffold, build, test, pack, install.           |
| `docs\device-security.md`                    | The one-page boundary checklist.                                     |
| `_plan\2.0-decisions.md` D02–D10, D20, D22b  | The standing product decisions.                                      |

## 1. Components and ownership

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

A package is one directory whose name equals the manifest `id`:

```text
<package id>\
  plugin.wsgm.json                     six fields; see the SDK reference
  <EntryAssembly>.dll                  AMD64 managed assembly with a CLR header
  <EntryAssembly>.deps.json            drives package-local dependency resolution
  *.dll                                package-local dependencies (host-first rule, §7)
  LICENSE.txt, THIRD_PARTY_NOTICES.md, PROVENANCE.md   as the package's licences require
  glyphs\profiles\<profileId>.json     glyph profiles (optional)
  glyphs\assets\<sha256>.svg|png       hash-addressed artwork (optional)
```

Budgets applied everywhere a package is validated, staged or packed (`Core\DevicePackagePolicy.cs`,
Device Lab `PluginPackageWorkflow`):

| Budget                                      | Value                                                   |
| ------------------------------------------- | ------------------------------------------------------- |
| Filesystem entries (files plus directories) | 1024, counted before sorting                            |
| Files                                       | 512                                                     |
| One file                                    | 128 MiB                                                 |
| Whole package                               | 512 MiB                                                 |
| Manifest read                               | 1 MiB read bound, then the SDK's 256 KiB document limit |
| Reparse points                              | none, anywhere in the tree                              |

### The protected slot

`Core\DeviceInstallationPaths.cs` derives everything from `%ProgramFiles%\WSGM`. A blank Program
Files answer from Windows throws rather than falling back.

| Path                                               | Role                                                    |
| -------------------------------------------------- | ------------------------------------------------------- |
| `%ProgramFiles%\WSGM\DevicePlugins\installed\<id>` | the one live package root                               |
| `%ProgramFiles%\WSGM\DevicePlugins\.staging`       | fixed staging sibling used by maintenance and setup     |
| `%ProgramFiles%\WSGM\DevicePlugins\.previous`      | the parked old slot during a replacement                |
| `DevicePlugins\.installed.previous`                | legacy recovery name; only the installer reconciles it  |
| `DevicePlugins\.installed.staging-*`               | legacy staging namespace; only the installer removes it |
| `DevicePlugins\reviewed`                           | legacy root; only the installer deletes it              |

Only the immediate children of `installed` are inventoried. The two fixed siblings sit beside
`installed`, not inside it, so normal discovery never sees them.

## 3. Startup and discovery

`Program.MainAsync` runs process-mode recovery (`--restore-shell`, `--unregister-shell`) before
logging, then the two plugin maintenance commands, then logging and the UAC and lock-screen
one-shots, then the cardinality gate, and only then the run-mode decision. The gate runs on the
entry thread before Avalonia creates its dispatcher, so the STA apartment is preserved.

The gate is skipped when the arguments are exactly `--overlay-test` (a mixed
`--shell --overlay-test` is still gated) or include any of `--restore-shell`, `--unregister-shell`,
`--set-uac-silent`, `--restore-uac`, `--disable-lock-on-wake`, `--restore-lock-on-wake`,
`--apply-steam-input-shim`, `--remove-steam-input-shim`, `--radio-probe`, `--uninstall-restore`,
`--setup`, `--install-device-plugin`, `--remove-device-plugin`.

The gate takes `Global\WSGM.DevicePackageSlot` for at most 5 s and runs
`DevicePackageStager.InventoryEffectiveInstalledPackage`. `installed` and `.previous` must each be
absent or a plain directory; `installed` is inventoried if it exists, otherwise `.previous` is
inventoried in its place without moving anything. Inventory treats a reparse-point root as one
unfollowed root, re-reads each entry's attributes (a vanished entry is an I/O failure, not
"absent"), keeps directories and sorts them case-insensitively.

| Outcome            | Behaviour                                                                                                                                                                                      |
| ------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Zero roots         | Core WSGM starts with Device Integration unavailable.                                                                                                                                          |
| One root           | Startup continues; no manifest is read yet.                                                                                                                                                    |
| More than one root | Exit code 2, a message box titled "WSGM Device Plugin startup refused", and the log line `Device plugin startup inventory: Multiple, roots=n` followed by every root's name and absolute path. |
| Gate timeout (5 s) | Exit code 2: "The protected Device Plugin slot remained busy during startup."                                                                                                                  |
| Inspection failure | Exit code 2: "WSGM could not inspect the protected Device Plugin slot. Use setup or --remove-device-plugin to repair it."                                                                      |

Real discovery happens later, inside the device cycle (§8), under the same slot gate.

## 4. Machine-wide synchronization

| Object                          | Kind                                                   | Held by                                                                                                                                | Purpose                                                    |
| ------------------------------- | ------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| `Global\WSGM.DevicePackageSlot` | named mutex, waited on                                 | startup inventory, every cycle start, maintenance, setup, uninstall                                                                    | No one loads or inventories a slot that is being replaced. |
| `Global\WSGM.DeviceOwner`       | named mutex created unowned; ownership is `createdNew` | the `DeviceCoordinator` for the process lifetime, maintenance for the whole operation, setup and uninstall, an attended Device Lab run | At most one hardware cycle on the machine.                 |
| `Local\WSGM.Shell`              | named mutex, initially owned                           | the shell instance                                                                                                                     | One shell per session; the installer probes it.            |

`Core\DevicePackageSlotGate.cs` waits on a dedicated thread named "WSGM device package slot gate"
because mutex ownership is thread-affine. It treats an abandoned mutex as acquired (crash recovery),
returns `null` on timeout, and releases from the owning thread on disposal.

The owner marker is never waited on: it is created unowned, and "already exists" means someone else
owns the hardware. The installer performs the same election with `CreateMutexW` and
`ERROR_ALREADY_EXISTS`. Who takes what:

- Normal shell: the coordinator creates the owner marker once for the process lifetime. When it
  already exists it logs
  `Device cycle: machine-wide ownership is already active or unavailable; no cycle started.` and no
  cycle ever starts. Each cycle start takes the slot gate (5 s) and releases it once the plugin has
  started.
- Maintenance (`--install-device-plugin`, `--remove-device-plugin`): after the elevation check, the
  slot gate (5 s), then the owner marker, both held through the whole filesystem operation. A live
  coordinator therefore refuses maintenance; close the shell first.
- Setup: slot gate, stop the logon service, stop instances, blocker check, owner marker, stale
  staging cleanup; both held through file copy and slot swap; owner released first, gate second.
- Uninstall: the same objects held through `[UninstallRun]` and `[UninstallDelete]`.

## 5. Package validation

`DevicePackagePolicy.ValidateInstalledPackage` runs on the single root discovery found and stops at
the first failure with a stable rejection code:

| Step | Check                                                                                                                           | Code                                                                |
| ---- | ------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------- |
| 1    | Root still exists and is a directory.                                                                                           | `package-invalid`                                                   |
| 2    | Root is not a reparse point.                                                                                                    | `package-link`                                                      |
| 3    | Bounded walk: entry, file, per-file and aggregate budgets; no reparse point anywhere.                                           | `package-invalid`                                                   |
| 4    | `plugin.wsgm.json` resolves under the root without traversal or links, read under 1 MiB.                                        | `package-invalid`                                                   |
| 5    | SDK `PluginManifestReader.Read` succeeds (size, depth, shape, six field rules).                                                 | `manifest-invalid`, or `api-incompatible` when `apiVersion` differs |
| 6    | `apiVersion` equals `DeviceApi.Version` (2).                                                                                    | `api-incompatible`                                                  |
| 7    | Entry assembly resolves under the root and is an AMD64 image with a CLR header, metadata and an assembly manifest (`PEReader`). | `architecture-unsupported`                                          |

The slot itself yields `multiple-package-roots`; an empty slot is `no-package-installed`. Validation
never loads the assembly.

## 6. Installing, replacing and removing

The maintenance commands are exact: `--install-device-plugin <expanded-directory>` with no other
argument, or `--remove-device-plugin` alone. A non-elevated process relaunches itself with the
`runas` verb and returns the child's exit code; failures exit 1.

`DevicePackageStager.StageAsync` performs the replacement while both machine-wide objects are held:

1. Refuse a source that lexically overlaps `installed`, `.staging` or `.previous` in either
   direction, or that traverses a link or reparse point at any ancestor, including when the leaf is
   missing.
2. Refuse a source whose `(volume serial, file id)` lineage aliases any protected path, so a
   junction or hard link cannot bypass the lexical check.
3. Open every ancestor and the source root with no-follow handles, hold them, and require the
   identity unchanged ("Package source changed while its path was being secured.").
4. Reconcile: delete a leftover `.staging`; if `installed` exists delete any `.previous`, otherwise
   move `.previous` back to `installed`. A missing source still reconciles before failing.
5. Read the manifest through the secured handle (ordinary file, 1 MiB bound, SDK rules) and require
   `id` to be a safe directory segment equal to its own file name.
6. Copy into `.staging\<id>` with the budgets enforced on enumeration and on bytes actually read,
   never following links, writing with `CreateNew` and write-through.
7. Re-run discovery on `.staging`; it must yield exactly one valid root.
8. Require `.previous` absent and `.staging` present, then publish atomically: move `installed` to
   `.previous`, move `.staging` to `installed`. If the second move fails and `installed` is absent,
   move `.previous` back and rethrow.
9. Delete `.staging` when not published, or `.previous` when published; a cleanup failure is only
   logged.

Success logs `Device plugin maintenance: installed <id> into the protected slot at <path>.`. Removal
validates the same attributes, then deletes `.staging`, `.previous` and `installed` last, so a
failed cleanup leaves the live package rather than a resurrected backup. It is idempotent on an
empty slot.

The installer's `ReplaceDevicePluginSlot` mirrors the transaction with the legacy names: it migrates
`.installed.previous` to `.previous`, refuses when both exist, retires `reviewed`, and restores the
previous slot when the swap fails. Deselecting the device component deletes every recovery root and
then `installed`.

`eng\dev-deploy.ps1` is different: it swaps through `<id>.incoming` and `<id>.old` inside
`installed` from an elevated child and takes neither named object, relying on having stopped WSGM
first. An interrupted swap leaves a second directory under `installed`, which the next startup
refuses as a second package root; remove the leftover by hand.

## 7. Loading the plugin

`Shell\PluginPackageLoader.cs` loads the validated package into a collectible `AssemblyLoadContext`
named `WSGM.Plugin:<directory name>` with an `AssemblyDependencyResolver` over the entry assembly's
`.deps.json`.

- The entry image is loaded from a stream so the installed file is not mapped for the context's
  lifetime; the package can be replaced as soon as the lifecycle is quiescent.
- The entry type must be public, concrete, non-generic, assignable to `IDevicePlugin`, with a public
  parameterless constructor. Its `PackageId` must equal the manifest `id`.
- Host-first resolution: the SDK assembly, `WinRT.Runtime` and `Microsoft.Windows.SDK.NET` are
  always answered from the host regardless of version. Every other assembly is asked of the default
  context first; the package copy is loaded only when the host has no copy or cannot satisfy the
  version, and that duplicate is logged once. Native libraries resolve through the package only and
  must stay under the root. The reason is in `device-integration.md`, "Host-first dependency
  resolution".
- A load failure disposes the plugin if it was created, unloads the context and rethrows.
- Unload is requested, not verified. `DevicePluginRuntime.DisposeAsync` calls `Unload()` only when
  command quiescence, the emergency stop and the plugin's `DisposeAsync` were all clean; otherwise
  the context stays loaded. Nothing waits for the GC to collect it.

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
| `Detected`            | A cycle start began; the slot is about to be inspected.                                                                                                               |
| `Passive`             | No valid package, or the plugin did not match the machine.                                                                                                            |
| `Activating`          | The runtime is loading, or a restart is scheduled.                                                                                                                    |
| `Active` / `Degraded` | The plugin's `PluginStartResult`.                                                                                                                                     |
| `Suspended`           | After a successful suspend.                                                                                                                                           |
| `Deactivating`        | During an intentional stop.                                                                                                                                           |
| `Faulted`             | Restart attempts exhausted, fault cleanup unverified, or a restart failed. Fails open: the virtual target and WSGM's HidHide entries are gone; desired state is kept. |

### Start

A cycle starts when integration is enabled at construction, when the master toggle turns on, after a
fault backoff, or on manual retry:

1. State `Detected`; take the slot gate (5 s). Timeout or failure schedules a start fault.
2. Under the gate: reconcile the slot, collect `DeviceMachineIdentity` from the registry SMBIOS
   keys, and run discovery. No valid package sets `Passive` and logs
   `Device cycle passive: <code>; packageRoots=<n>.`.
3. Advance the cycle generation; state `Activating`; load the package (§7). The gate is released
   after this step.
4. Attach the runtime to the coordinator, the capability router, the OEM router and the settings
   coordinator. Then allowlist WSGM in HidHide before the plugin starts, because a plugin cannot
   discover a controller that another tool's allowlist hides from WSGM.
5. `client.StartAsync` with a 15 s deadline: `DetectAsync` (no match publishes `Passive` with the
   plugin's reason), then `StartAsync` with the host adapter, generation, definition id, the state
   directory `%LOCALAPPDATA%\WSGM\DeviceState\<packageId>` and the controller-management flag. A
   plugin exception publishes `Degraded` with `TransportFaulted` and rethrows.
6. Record the definition id, attach plugin settings, import glyph profiles, reset the restart
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

Session lock and system suspend trigger suspend; unlock and resume trigger resume. The shell
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
reconciliation keeps `User` so a restored power limit still pauses AutoTDP. The origin decides both
whether AutoTDP steps aside and whether the value is saved (§11), so a restore must never claim to
be one.

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
to the global or per-application performance profile.

Debouncing lives in the controls, not the router: the slider commits 250 ms after the last change,
and the colour editor writes only on Apply (colour, then brightness).

## 11. Desired state, settings and profiles

### Desired-value layers

`DeviceDesiredStateResolver` resolves one `DeviceCapabilityPreference` per capability with the
precedence application override, hardware profile, AC or DC policy by power source, global default,
none. The values live under `DeviceIntegration.Profiles[]`, keyed by the machine's identity key, so
swapping plugins keeps the machine's preferences. Reconciliation applies them after a hardware
profile is selected: lower limits first when lowering, raise the fast limit first when raising, skip
values equal to the readback, and log one summary line.

`DeviceCoordinator.PersistUserCapabilityValueAsync` is what fills these layers in. Every `User`
write that the device accepted is stored by `DeviceDesiredStateWriter` into the layer a control
press means: the running application's override when a game is running, the global default otherwise
— the same rule `CycleAuthoredProfileAsync` uses, because mid-game a user is configuring what they
are playing and on the desktop there is no per-game scope to mean. The AC, DC and named-profile
layers are not written from a control press; they resolve but are authored elsewhere, and a press
landing in a layer the user cannot see they used would be worse than not saving. A
running-application change updates the router's context and reconciles, so a value saved for a game
is applied when it launches and the global one comes back when it exits.

Two roles are deliberately excluded, because `AppConfig.Performance` already stores them and also
decides how each is released when an application closes: `PowerSustainedLimit` and
`VariableRefreshRate`. Their manual writes reach that owner through `AttachAutoTdpManualOverride`
and `AttachManualVariableRefreshOverride`, which the shell session roots, so the overlay row and
Steam's own control save to one place instead of two. Reconciliation keeps the `User` origin so a
restored power limit still pauses AutoTDP; the funnel therefore skips any value that already equals
what the layers resolve to, which is also what stops a control landing back on its starting value
from writing configuration.

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
`lighting.zone-color` and selects in the overlay. `Core\DeviceProfileSelectionStore.cs` writes only
which profile is selected, globally or per application. `Core\DeviceProfileValidation.cs` checks a
curve against the live descriptor at apply time (`CapabilityAbsent`, `NotACurve`, `PointCount` 1–64,
`NotAscending`, `OutOfBounds`). `Shell\DeviceProfileApplier.cs` resolves, validates, builds the
curve value and executes with a 5 s timeout, counting `AppliedUnverified` as success and a timeout
as failure. A selection naming a deleted profile resolves to nothing and reads `MISSING` in the
overlay. Curve editing goes through `CurveEditing` (at most 64 points, minimum input gap 1, a 0–100
plane), so an invalid curve cannot be built. The reasons are in `device-integration.md`, "Authored
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
3. Resolve the target: the first per-application override whose id equals the running application,
   else the global default (`SteamDeckComposite` by default).
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
`OemAction` vocabulary stored under `DeviceIntegration.Profiles[].OemAssignments`:

| Action                                                    | Effect                                                                |
| --------------------------------------------------------- | --------------------------------------------------------------------- |
| `ToggleWsgmOverlay`                                       | Toggle the overlay.                                                   |
| `ToggleSteamQuickAccess`                                  | Send Big Picture's Quick Access shortcut when Big Picture is visible. |
| `ShowWsgmDevicePage`                                      | Open the overlay's Device page.                                       |
| `ToggleWsgmTaskbar`                                       | Toggle the Open apps strip.                                           |
| `ToggleDesktopGameMode`                                   | Enter Game Mode if Explorer runs, else Desktop Mode.                  |
| `ToggleOnScreenKeyboard`                                  | Toggle the touch keyboard.                                            |
| `CyclePerformanceProfile`, `CyclePerformanceOverlayLevel` | RTSS cycles.                                                          |
| `VirtualTargetRearButton1`, `VirtualTargetRearButton2`    | Pulse a rear paddle on the target.                                    |

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

The overlay's Device destination (`DeviceOverlayBridge`, `DeviceOverlaySectionPages`) shows
plugin-declared sections first, in declared order, dropping empty ones, then WSGM's fixed sections
`Overview`, `PowerAndThermals`, `ControllerAndMotion`, `Oem`, `LightingAndFeatures`, `Diagnostics`.
An unplaced capability lands in the section its role implies. WSGM's own rows join them: AutoTDP,
hardware profile and authored profile under power; controller target and glyph selection, plus the
glyph preview and input test, under controller; recovery under diagnostics.

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

## 15. Glyphs

At cycle start the coordinator imports `glyphs\` through the SDK importer, logs
`Device glyph catalog: package=…, profiles=…, rejected=…`, and stores the profiles in
`PhysicalGlyphCatalog`. Selection follows the `GlyphSelection` setting: `Automatic` picks the
ordinal-first profile whose `ExactDeviceIds` contain the matched definition; `NativeSteam` disables;
a manual id that does not match falls back to automatic and reports it. Any fallback leaves Valve's
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

| Key                           | Default              | Effect                                                                                                                                                                                     |
| ----------------------------- | -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `Enabled`                     | false                | Master switch. Off to on starts a fresh cycle; on to off stops with `IntegrationDisabled` and must verify teardown.                                                                        |
| `ControllerManagementEnabled` | false                | Child preference, remembered while the master is off; toggled live through the 6 s path.                                                                                                   |
| `ControllerTarget`            | `SteamDeckComposite` | Global default target (`Xbox360`, `DualShock4` selectable).                                                                                                                                |
| `ControllerTargets`           | `[]`                 | Per-application target overrides keyed by canonical application id.                                                                                                                        |
| `AutoTdpEnabled`              | false                | Runs only with the master on.                                                                                                                                                              |
| `GlyphSelection`              | `Automatic`          | `Automatic`, `NativeSteam`, or a manual profile.                                                                                                                                           |
| `ManualGlyphProfileId`        | null                 | The manual profile id.                                                                                                                                                                     |
| `Profiles[]`                  | `[]`                 | Per-machine desired values, selected hardware profile and OEM assignments, keyed by the identity key (24 hex characters of SHA-256 over manufacturer, baseboard product and version, SKU). |
| `PluginSettings[]`            | `[]`                 | Per plugin and device definition: stored values, cached declaration, authored profiles, profile selections.                                                                                |

Loading repairs bad enum names so one bad value cannot quarantine the file. Normalization trims ids,
drops blank or duplicate entries, drops invalid cached declarations and non-ascending curves, and
keeps a selection naming a deleted profile so it stays diagnosable. Reload replaces the config
object and calls `ApplyConfigAsync`; coordinator-originated changes persist through
`ConfigStore.Mutate` under the transition gate.

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

`external\WSGM.Device.Msi.Claw8A2Vm` (MIT) is the reference plugin and the shape every rule above
was tested against. Its manifest is `wsgm.device.msi.claw-8-a2vm`, API 2, entry
`WSGM.Device.Msi.Claw8A2Vm.Claw8A2VmPlugin`. It targets `net10.0-windows10.0.19041.0`, references
only the SDK and `System.Management`, ships its licence and notices beside the assembly, declares no
settings manifest, and keeps every vendor address inside the package.

Identity: `DetectAsync` matches SMBIOS manufacturer `MICRO-STAR INTERNATIONAL CO., LTD.`, baseboard
`MS-1T52` and SKU `1T52.1` and returns definition id `ms-1t52`. Start re-reads identity and gates
the WMI firmware (`Get_EC` prefix `1T52EMS1.109`) and the MCU revision (`0229`); a mismatch leaves
those services unavailable with `FirmwareNotVerified`.

Transports: `MSI_ACPI` over WMI with 32-byte packages, a 3 s per-operation timeout and a required
status byte; the `MSI_Event` WMI event source for the front buttons; a HID vendor collection for the
MCU (profile read and write, mode switch with a 1 s acknowledgement and 50 ms topology polling); the
HID gamepad collection for DirectInput reports at about 125 Hz; the WinRT gyrometer at a 10 ms
report interval; Intel IGCL through `ControlLib.dll` for Arc Sync; and a low-level keyboard hook
that suppresses the firmware's orphan key-up chords and fails open.

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

The declared sections are `power` (icon Power; categories limits, charging, control titled "Fans"),
`lighting` (category zones) and `info` (icon Gauge; categories ownership, readings). Fan RPM is
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
cycle requests the gyro's 10 ms and accelerometer's 2 ms driver minima, polls every 2 ms, and
restores the prior intervals on release when still owned. This part's gyro carries a real zero-rate
offset that Intel ISS does not remove and no controller target corrects, so
`StationaryGyroBiasCalibrator` measures it from 200-report rest windows — gated on rate span,
acceleration span and gravity magnitude — and subtracts it. Subtraction only: a deadband or a
zero-hold would replace the drift with a dead zone around rest. Readings older than 50 ms stop
contributing angular velocity and the frame average preserves their area. Motion writes no
per-report file and emits no per-sample line into `wsgm.log`; only the measured offset and read
failure transitions are logged. No acceleration or orientation is synthesized.

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

Glyphs: one profile `claw-8-a2vm` for `ms-1t52`, 23 hash-locked assets (20 control SVGs at 32×32,
one full-controller SVG, left and right PNGs at 643×464), 20 control mappings with the printed
labels, no aliases, notice `THIRD_PARTY_NOTICES.md`.

Tests build the plugin with fake WMI, MCU, controller, motion, chord and event services and the
SDK's `TestPluginHostAdapter`. Packaging: `eng\pack.ps1` publishes framework-dependent `win-x64`,
strips symbols, copies `glyphs\` verbatim, runs `wsgm-device validate` and `wsgm-device pack`.
WSGM's `eng\stage-device-components.ps1` publishes Device Lab, invokes that packer, checks the
archive's path safety, extracts to `Packages\<id>`, requires the licence, notices and provenance
files, compares the staged glyph count with the source tree, and validates again. The installer
copies `Packages\*` into `.staging` and swaps the slot during post-install.

## 19. Device Lab

`wsgm-device` (`external\WSGM.DeviceLab`, MIT) is the authoring and diagnostic tool. No argument
opens the GUI; every command prints camelCase JSON to stdout, diagnostics to stderr, and exits 0, 64
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
