# Host and debugging

## Host topology

```text
ShellSession
  -> DeviceCoordinator (machine owner and cycle orchestration)
  -> PluginPackageCatalog -> PluginLoader (collectible PluginLoadContext)
  -> DevicePluginRuntime -> IDevicePlugin
  -> host adapter publications
  -> capability/settings/OEM/controller/glyph consumers
```

Packages are `.wsgmpkg` files in `%ProgramFiles%\WSGM\Plugins`, device and common alike, read by
`Core/PluginPackageCatalog`. Zero or one device package id is allowed. With zero, device-independent
WSGM stays usable. With more than one, device integration stays passive and WSGM starts normally.
Loading happens only when Device Integration is enabled.

The Shell owns the common `PluginHost`, which refuses the Device category. DeviceCoordinator
preserves controller-release ordering and calls its dedicated runtime directly. The common host
retains category capacity after an uncertain stop/disposal and retains a timed-out lifecycle lane
until the actual task ends. `CommonPluginManager` takes common packages from the same catalog. It
starts configured enabled instances, plus an implicit default GPU instance when a matching adapter
exists and no explicit instance choice overrides it, independently of Device Integration. The
Settings Plugin tab edits activation. `eng/new-plugin.ps1` and `eng/package-plugin.ps1` scaffold and
package common plugins. See `docs/plugin-system.md` for health generations, resident mode revisions
and the remaining UI follow-ons.

Important owners:

- `Global\WSGM.DeviceOwner` is an unowned named-mutex marker: `createdNew` elects the owner and the
  handle lifetime holds the claim. Never wait on or release it as a thread-owned mutex.
- `Local\WSGM.Shell` admits one shell session.

The coordinator keeps the device-owner marker while the shell runs even if Device Integration is
disabled. Close WSGM before replacing a package file: a loaded file is held open, and toggling
integration off is not owner release.

## Package and load boundary

`Core/PluginPackageFile` opens a package with `FileShare.Read` and reads it into memory. It rejects
links, unsafe entry names, limit violations and native images; the catalog then rejects an API
mismatch and non-x64 entry code, and the loader an invalid entry type or package ID. For one id the
highest version wins and older files are reported as superseded, never deleted.

The loader keeps the file open while its code may run and loads the entry and package dependencies
from memory. `WSGM.Device.Sdk`, `WSGM.Plugin.Sdk`, SteamUiToolkit, `WinRT.Runtime` and
`Microsoft.Windows.SDK.NET` are host-owned (`PluginLoadContext.HostOwned`) and always bind to the
host copy. A second WinRT pair would break process-global CsWinRT initialization. Other managed
dependencies try the default context first. If the host copy does not satisfy the request, the
package copy loads and a warning is logged. Packages carry no native code; native resolution is the
system's.

Dependency isolation is not fault isolation. A fatal managed/native plugin failure can terminate
WSGM, and `AssemblyLoadContext.Unload()` is only a request. Dirty cleanup deliberately retains the
context rather than claiming a verified unload.

## Lifecycle and deadlines

- Device-owner reservation is an immediate named-marker election, not a waited package-slot lock.
- Start: 15 seconds.
- Suspend/resume: 5 seconds.
- Controller-management toggle: 6 seconds.
- Normal stop: 15 seconds.
- Start cleanup/emergency runtime cleanup: about 5 seconds.

Fault reporting closes admission, cancels work, releases the controller (keeping the physical pad
hidden, because the restart takes it again at once), and tears down. An unverified cleanup step is
logged and never blocks the restart. Faults restart at most twice, after roughly one and four
seconds; exhaustion enters `Faulted` and shows the physical pad again.

Full teardown is best effort and keeps evidence. It closes admission, enters `Deactivating`,
releases the controller, calls plugin stop, detaches and withdraws publications, disposes, and ends
in `Disabled`. Failures accumulate, so one bad subscriber cannot skip later restoration. During
application shutdown, AutoTDP must stop before the coordinator, because its restore still needs the
capability path.

## Publication and command behavior

- Adapter calls execute synchronously on the plugin's publishing thread. The adapter catches
  consumer exceptions and logs `device-plugin-publication-<channel>`; a returned publish call is not
  proof that the production router accepted the record.
- Production validates the cycle and descriptor generations, and the complete descriptor layout,
  including `CapabilityLayout`, `DevicePowerPreset` and `DevicePowerPair`. It also checks IDs, value
  shapes, bounds, sequence, timestamps and freshness. Freshness is five seconds for telemetry and
  fan RPM, five minutes for charge and lighting, and thirty seconds for everything else.
- Capability lanes serialize per capability key; a plugin must also serialize a transport shared by
  different capability keys.
- Caller timeout can return `TimedOut` or `Indeterminate` while the device call finishes later. The
  host reconciles a late result only when runtime, command ID, and both generations still match.
- Automatic desired-value restoration goes through `DeviceDesiredWriteAdmission.TryAdmit`. It needs
  an available state that is neither stale nor faulted (a never-read-back `Unknown` state is still
  restored), an observed value that differs, and no pending command; the same value is not repeated
  after an uncertain result, but a different one goes ahead without waiting for a readback. Lighting
  also goes through `DeviceLightingRestore.TryBegin`, which allows at most three attempts per zone,
  value and cycle. Both use the `DesiredStateRestore` origin, never `User`. That origin never
  persists, but restoring a sustained power limit still pauses AutoTDP. Readback updates effective
  state only and must not reach configuration persistence. User-origin writes may update desired
  state and pause AutoTDP. Always preserve the origin.

## Evidence ladder

Logs are `%LOCALAPPDATA%\WSGM\wsgm.log`, rotated to `wsgm.old.log` at 5 MB (`docs/logging.md`).
Verbose logging is off by default. Without `--verbose` or Settings > System > Diagnostics > Verbose
logging, `PluginTrace.Debug` output is dropped. `Log.Change` lines print only on a transition and
then report `(previous state held for N more polls)`. Device plugin state lives in
`%LOCALAPPDATA%\WSGM\DeviceState\<packageId>`. The complete line and change-key index is the
diagnostics table in `docs/device-plugin-system.md`.

Work through the exact failing run:

1. Record the WSGM and package versions, the machine, scenario and settings, and a narrow timestamp
   range.
2. Read catalog errors and any `Device cycle passive:` refusal; multiple device IDs leave WSGM
   running.
3. Separate machine-owner denial from integration-disabled or passive state:
   `Device cycle passive: <code>; devicePackages=<n>.` versus
   `Device cycle active: package=…, state=…`.
4. Follow `Device cycle: state=...`, cycle generation, and matched/cleared definition.
5. Inspect loader/start errors for API, architecture, entry type, package ID, dependency, and WinRT
   failures.
6. Inspect `plugin/<scope>:` and the precise availability reason.
7. Look for publication rejections:
   - `Device descriptor set rejected`;
   - `Device capability state rejected`;
   - `Device capability delta rejected: key=…, reason=OutOfOrder.`, logged once per transition;
   - publication-consumer failures;
   - settings manifest refusal or delivery errors.
8. Follow `Device command: capability=..., outcome=..., rollback=...` and any
   `Late device command result reconciled: command=…` or `Late device command result ignored:` line.
9. Follow `Device plugin restart n/2 scheduled`, `Device cycle faulted after restart exhaustion`,
   `Controller released: scope=…, physicalKeptHidden=…` with any `Controller release: …` warning
   before it, plugin stop, and incomplete-cleanup evidence.
10. For input, check HidHide readability/ledger, `Managed controller target created/kept/replaced`,
    `Controller forwarding paused for suspend` and `Controller forwarding resumed: …` around a wake,
    the plugin's `controller` traces (a missing pad is Degraded and waited for, not a fault),
    whether the haptic sink holds published capabilities, and explicit stop.

The Settings diagnostics pipe `WSGM.DeviceCoordinator.<sessionId>` is a read-only summary of
package, generation, cycle, and capability counts. It does not expose the complete detection or
lifecycle reason.

Detection runs before the plugin can install `PluginTrace`. The coordinator's no-match retirement
line is `Device detection passive: package=…; runtime retired.`. To learn why, compare identity with
Device Lab (`wsgm-device candidates` or `test plugin`).

## Current lifecycle checks

Recheck source before diagnosing an old regression as current behavior:

- The runtime retains the latest settings manifest in `SettingsManifest`.
  `PluginSettingsCoordinator.Attach` subscribes and immediately reads it with `OnManifest`, so a
  manifest published during `StartAsync` is delivered after the matched definition is known.
- A no-match detection is retired immediately. `DevicePluginRuntime.StopCoreAsync` skips the
  plugin's stop when start was never attempted;
  `PassiveDetectionRetiresTheRuntimeWithoutStartingOrStoppingThePlugin` covers this path. Do not
  treat a passive package as a live device cycle.
- On resume the router adopts the new capability cycle before validating descriptors. Controller
  management re-enable keeps the cycle; descriptor generation can restart at one only in a new
  cycle. See `DevicePluginRuntimeTests` for the publication/lifecycle ordering fixtures.
- Desired-state admission and lighting attempt limits are covered by
  `DeviceDesiredWriteAdmissionTests` and `DeviceLightingRestoreTests`. Inspect the command origin:
  restoration uses `DesiredStateRestore` and never creates a saved user edit.

Desired values are profile values (`docs\profiles.md`): the running game's enabled profile, then
Global, then none. Restoration uses `DesiredStateRestore`, never `User`, and runs once per cycle
activation. Lighting readiness admits at most three attempts per zone, value and cycle; a refused
write may be retried, and an uncertain one is not repeated with the same value. Readback updates
effective state only; it must not enter configuration persistence.

Also check these proven regression patterns: duplicate SDK/WinRT loading, HidHide hiding discovery,
DOS/NT path duplication, state published before fresh-generation descriptors, whole-set omission,
failed haptic writes cached as success, uncertain writes retried, TestKit passing while production
rejects, and high-rate trace spam hiding the first transition.

## File routes

Paths are under `src/WSGM/` unless another project is named.

| Concern                      | Start here                                                                                                                                                                                           |
| ---------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Host mechanism and rationale | `docs/device-plugin-system.md`, `docs/device-integration.md`, `docs/plugin-system.md`                                                                                                                |
| SDK contract                 | `src/WSGM.Device.Sdk/docs/reference.md`, `src/WSGM.Device.Sdk/`                                                                                                                                      |
| Package files and discovery  | `Core/PluginPackageFile.cs`, `PluginPackageCatalog.cs`, `src/Shared/Install/InstallLayout.cs`                                                                                                        |
| Load and lifecycle           | `Shell/DeviceCoordinator.cs`, `DevicePluginRuntime.cs`, `PluginLoader.cs`; common lifecycle in `PluginHost.cs`                                                                                       |
| Common plugins               | `Shell/CommonPluginManager.cs`, `CommonPluginDependencyPlan.cs`, `CommonPluginSettings.cs`; `src/WSGM.Plugin.Ir`                                                                                     |
| Publications and commands    | `Shell/DeviceCapabilityRouter.cs`, `PluginSettingsCoordinator.cs`, `DeviceOemActionRouter.cs`                                                                                                        |
| Desired state and restore    | `Shell/DeviceDesiredWriteAdmission.cs`, `DeviceLightingRestore.cs`, `DeviceProfileApplier.cs`                                                                                                        |
| Power and AutoTDP            | `Core/AutoTdp.cs`, `Shell/AutoTdpService.cs`, `DevicePowerPresets.cs`, `DevicePowerAssignments.cs`, `NativeQamPowerPresetService.cs`                                                                 |
| Windows power schemes        | `Core/PowerSchemes.cs`, `Core/WindowsPowerPolicy.cs`, `Overlay/PowerSchemeView.cs` (work with Device Integration off)                                                                                |
| Diagnostics and identity     | `Shell/DeviceCoordinatorDiagnostics.cs`; `src/WSGM.Install/DeviceMachineIdentity.cs`                                                                                                                 |
| Controller safety            | `Shell/ControllerManager.cs` (`ReleaseAsync`), `HidHideOwnership.cs`, `PluginHapticSink.cs`, `Input/ManagedUiPad.cs`                                                                                 |
| Target input/output          | `Input/ManagedControllerRouter.cs`, `ControllerOutputRouter.cs`, `IControllerTargetBackend.cs`, `ViiperControllerBackend.cs`, `Xbox360Report.cs`, `DualShock4Report.cs`, `SteamDeckNeptuneReport.cs` |
| Host consumers               | `Shell/DeviceOverlayBridge.cs`; `Core/DeviceConfiguration.cs`, `PhysicalGlyphCatalog.cs`                                                                                                             |
| Reference plugin             | `src/WSGM.Device.Msi.Claw/`: `ClawCapabilities.cs`, `ClawServices.cs`, `ClawControllerService.cs`, `ClawPlugin*.cs`, `ClawRecoveryJournal.cs`, `MsiWmiPlatform.cs`                                   |
| Reference plugin tests       | `tests/WSGM.Device.Msi.Claw.Tests/ClawPluginTests.cs`                                                                                                                                                |

## Focused tests

Run these after the maintainer's manual test, as the root validation policy requires, with
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~<Class>"`:

- Shell: `DevicePluginRuntimeTests`, `DeviceCoordinatorConcurrencyTests`,
  `DeviceCapabilityRouterTests`, `DeviceIntegrationOffTests`, `ControllerManagerTests`,
  `HidHideOwnershipTests`, `DeviceDesiredWriteAdmissionTests`, `DeviceLightingRestoreTests`,
  `DeviceProfileApplierTests`, `AutoTdpServiceTests`, `PluginHostTests`,
  `PluginSettingsProjectionTests`, `DeviceCoordinatorDiagnosticsTests`, `DevicePowerPresetsTests`.
- Core: `PluginPackageCatalogTests` (package files, discovery and the glyph source),
  `DeviceDesiredStateTests`, `OemActionPolicyTests`, `PluginSettingsResolverTests`.
- `PluginTraceTests` lives in `tests/WSGM.Device.Sdk.Tests/Plugin`.

Also run the affected SDK, plugin and Device Lab projects.
