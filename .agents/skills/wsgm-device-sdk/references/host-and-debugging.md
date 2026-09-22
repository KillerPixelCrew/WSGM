# Host and debugging

## Host topology

```text
Program package-cardinality preflight
  -> ShellSession
  -> DeviceCoordinator (machine owner and cycle orchestration)
  -> PluginPackageLoader (nested collectible PluginLoadContext)
  -> PluginHost registration -> DevicePluginCompatibilityAdapter -> DevicePluginRuntime -> IDevicePlugin
  -> host adapter publications
  -> capability/settings/OEM/controller/glyph consumers
```

Exactly zero or one immediate Device package root is allowed. With zero, device-independent WSGM
stays usable. With more than one, startup is refused before normal UI or plugin execution. Full
validation and loading happen only when Device Integration is enabled.

The Shell owns the common `PluginHost`. DeviceCoordinator preserves controller-release ordering and
uses its registration for lifecycle calls. An uncertain stop or disposal retains category capacity;
a timed-out call retains its lifecycle lane until the actual task ends. `CommonPluginManager`
discovers `%ProgramFiles%\WSGM\Plugins\<id>` plus the bundled `<app>\Plugins` catalog, where Artwork
ships. It starts only instances enabled in `AppConfig.PluginInstances` and owns their config
refresh, power transitions and shutdown independently of Device Integration. The Settings Plugin tab
edits activation. `eng/new-plugin.ps1` and `eng/package-plugin.ps1` scaffold and package common
plugins. See `docs/plugin-system.md` for health generations, resident mode revisions and the
remaining UI follow-ons.

Important owners:

- `Global\WSGM.DevicePackageSlot` is a real thread-owned mutex with a five-second acquisition
  budget. Startup inventory, discovery, maintenance, setup, and uninstall use it.
- `Global\WSGM.DeviceOwner` is an unowned named-mutex marker: `createdNew` elects the owner and the
  handle lifetime holds the claim. Never wait on or release it as a thread-owned mutex.
- `Local\WSGM.Shell` admits one shell session.

The coordinator keeps the device-owner marker while the shell runs even if Device Integration is
disabled. Close WSGM before package maintenance; toggling integration off is not owner release.

## Package and load boundary

The protected package root is `%ProgramFiles%\WSGM\DevicePlugins\installed\<package-id>`. Its
transactional `.staging` and `.previous` folders sit beside `installed`, not inside it. Validation
rejects links and reparse points, unsafe paths, limit violations, an API mismatch, non-x64 entry
code, and an invalid entry type or package ID. With zero package roots, the stager falls back to
inventorying `.previous`, so a leftover `.previous` can still load a package.
`DevicePackagePolicy.Inventory` counts every immediate subdirectory of `installed`, so a stray
folder, such as one left by an interrupted swap, causes a `Multiple` refusal at startup.

The loader streams the entry assembly and resolves package dependencies through
`AssemblyDependencyResolver`. `WSGM.Device.Sdk`, `WSGM.Plugin.Sdk`, SteamUiToolkit, `WinRT.Runtime`
and `Microsoft.Windows.SDK.NET` are host-owned (`PluginLoadContext.HostOwned`) and always bind to
the host copy. A second WinRT pair would break process-global CsWinRT initialization. Other managed
dependencies try the default context first. If the host copy does not satisfy the request, the
package copy loads and a warning is logged. Native dependencies stay confined to the package.

Dependency isolation is not fault isolation. A fatal managed/native plugin failure can terminate
WSGM, and `AssemblyLoadContext.Unload()` is only a request. Dirty cleanup deliberately retains the
context rather than claiming a verified unload.

## Lifecycle and deadlines

- Slot acquisition: 5 seconds.
- Start: 15 seconds.
- Suspend/resume: 5 seconds.
- Controller-management toggle: 6 seconds.
- Normal stop: 15 seconds.
- Start cleanup/emergency runtime cleanup: about 5 seconds.

Fault reporting closes admission, cancels work, makes the controller safe, and tears down. Clean
faults restart at most twice, after roughly one and four seconds; exhaustion enters `Faulted`.

Full teardown is best effort and keeps evidence. It closes admission, enters `Deactivating`, makes
the controller safe, calls plugin stop, detaches and withdraws publications, and disposes. It enters
`Disabled` only when the cleanup evidence allows it. Failures accumulate, so one bad subscriber
cannot skip later restoration. During application shutdown, AutoTDP must stop before the
coordinator, because its restore still needs the capability path.

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
  an observed or verified state, no pending command, and no previous uncertain result. Lighting also
  goes through `DeviceLightingRestore.TryBegin`, which allows one attempt per value and cycle. Both
  use the `DesiredStateRestore` origin, never `User`. That origin never persists, but restoring a
  sustained power limit still pauses AutoTDP. Readback updates effective state only and must not
  reach configuration persistence. User-origin writes may update desired state and pause AutoTDP.
  Always preserve the origin.

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
2. Find `Device plugin startup inventory:` and any gate or cardinality refusal.
3. Separate machine-owner denial from integration-disabled or passive state:
   `Device cycle passive: <code>; packageRoots=<n>.` versus
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
   `Controller make-safe: scope=…, step=…, result=…`, plugin stop, and incomplete-cleanup evidence.
10. For input, check HidHide readability/ledger, target generation, stale samples, haptic ownership,
    and explicit stop.

The Settings diagnostics pipe `WSGM.DeviceCoordinator.<sessionId>` is a read-only summary of
package, generation, cycle, and capability counts. It does not expose the complete detection or
lifecycle reason.

The `DetectAsync` no-match reason is never logged. Detection runs before the plugin can install
`PluginTrace`, and the host logs only the resulting state. A no-match therefore appears only as
`Device cycle active: …, state=Passive`. To learn why, compare identity with Device Lab
(`wsgm-device candidates` or `test plugin`).

## Current code-inspection traps

These have no focused regression test. Recheck them before blaming plugin code:

- `PluginSettingsCoordinator.Attach` runs in `DeviceCoordinator` after the plugin's own `StartAsync`
  (through `_pluginRegistration.StartAsync`) has returned. The runtime does not replay a manifest
  published during start, and `Attach` clears the cached manifest. A missing initial settings page
  can therefore be host ordering rather than a missing publication.
- After a Passive (no-match) cycle, teardown still requests controller release and plugin stop. The
  runtime's `EnsureLifecycleActive` guard turns that into
  `Controller make-safe: the plugin release was unverified: The device plugin is not active.` and
  `StopAsync` also calls the never-started plugin's `StopAsync`. Treat this as host noise.

These used to be traps and are now covered by tests:

- On resume and controller-management re-enable, the router adopts the runtime's new cycle before
  validating its first descriptor publication. The coordinator's post-call synchronization keeps
  that accepted readback, and descriptor generation can restart at one
  (`DevicePluginRuntimeTests.ResumePublishesFreshLightingIntoTheRouterBeforeTheLifecycleCallReturns`).
- Desired-state admission and lighting restore are covered by `DeviceDesiredWriteAdmissionTests` and
  `DeviceLightingRestoreTests`. The choice of `DesiredStateRestore` over `User` still has no test of
  its own.

Also check these proven regression patterns: duplicate SDK/WinRT loading, HidHide hiding discovery,
DOS/NT path duplication, state published before fresh-generation descriptors, whole-set omission,
failed haptic writes cached as success, uncertain writes retried, TestKit passing while production
rejects, and high-rate trace spam hiding the first transition.

## File routes

Paths are under `src/WSGM/` unless another project is named.

| Concern                       | Start here                                                                                                                                                     |
| ----------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Host mechanism and rationale  | `docs/device-plugin-system.md`, `docs/device-integration.md`, `docs/device-security.md`, `docs/plugin-system.md`                                               |
| SDK contract                  | `src/WSGM.Device.Sdk/docs/reference.md`, `src/WSGM.Device.Sdk/`                                                                                                |
| Package preflight/maintenance | `Program.cs`, `Core/DevicePackagePolicy.cs`, `DevicePackageSlotGate.cs`, `DevicePackageStager.cs`, `DeviceInstallationPaths.cs`                                |
| Load and lifecycle            | `Shell/DeviceCoordinator.cs`, `DevicePluginRuntime.cs`, `DevicePluginCompatibilityAdapter.cs`, `PluginHost.cs`, `PluginPackageLoader.cs`                       |
| Common plugins                | `Shell/CommonPluginManager.cs`, `CommonPluginPackage.cs`, `CommonPluginCatalog.cs`, `CommonPluginSettings.cs`; `src/WSGM.Plugin.Artwork`, `src/WSGM.Plugin.Ir` |
| Publications and commands     | `Shell/DeviceCapabilityRouter.cs`, `PluginSettingsCoordinator.cs`, `DeviceOemActionRouter.cs`                                                                  |
| Desired state and restore     | `Shell/DeviceDesiredWriteAdmission.cs`, `DeviceLightingRestore.cs`, `DeviceProfileApplier.cs`                                                                  |
| Power and AutoTDP             | `Core/AutoTdp.cs`, `Shell/AutoTdpService.cs`, `DevicePowerPresets.cs`, `DevicePowerAssignments.cs`, `NativeQamPowerPresetService.cs`                           |
| Windows power schemes         | `Core/PowerSchemes.cs`, `Interop/WindowsPowerSchemeApi.cs`, `Overlay/PowerSchemeView.cs` (work with Device Integration off)                                    |
| Diagnostics and identity      | `Core/DeviceCoordinatorDiagnostics.cs`, `DeviceMachineIdentity.cs`                                                                                             |
| Controller safety             | `Shell/ControllerManager.cs`, `ControllerMakeSafe.cs`, `HidHideOwnership.cs`, `PluginHapticSink.cs`                                                            |
| Target input/output           | `Input/ManagedControllerRouter.cs`, `ViiperControllerBackend.cs`, `Xbox360Report.cs`, `DualShock4Report.cs`, `SteamDeckNeptuneReport.cs`                       |
| Host consumers                | `Shell/DeviceOverlayBridge.cs`; `Core/DeviceConfiguration.cs`, `PhysicalGlyphCatalog.cs`                                                                       |
| Reference plugin              | `src/WSGM.Device.Msi.Claw8A2Vm/`: `ClawCapabilities.cs`, `ClawResources.cs`, `Claw8A2VmPlugin.cs`, `ClawRecoveryJournal.cs`, `MsiWmiPlatform.cs`               |
| Reference plugin tests        | `tests/WSGM.Device.Msi.Claw8A2Vm.Tests/ClawPluginTests.cs`                                                                                                     |

## Focused tests

Run these after the maintainer's manual test, as the root validation policy requires, with
`dotnet test tests\WSGM.Tests\WSGM.Tests.csproj --filter "FullyQualifiedName~<Class>"`:

- Shell: `DevicePluginRuntimeTests`, `DeviceCoordinatorConcurrencyTests`,
  `DeviceCapabilityRouterTests`, `DeviceIntegrationOffTests`, `ControllerMakeSafeTests`,
  `ControllerManagerTests`, `HidHideOwnershipTests`, `DeviceDesiredWriteAdmissionTests`,
  `DeviceLightingRestoreTests`, `DeviceProfileApplierTests`, `AutoTdpServiceTests`,
  `PluginHostTests`, `PluginSettingsProjectionTests`, `DeviceCoordinatorDiagnosticsTests`,
  `DevicePowerPresetsTests`.
- Core: `DevicePackagePolicyTests` (which also covers the slot gate and stager),
  `DeviceDesiredStateTests`, `OemActionPolicyTests`, `PluginSettingsResolverTests`.
- `PluginTraceTests` lives in `tests/WSGM.Device.Sdk.Tests/Plugin`.

Also run the affected SDK, plugin and Device Lab projects.
