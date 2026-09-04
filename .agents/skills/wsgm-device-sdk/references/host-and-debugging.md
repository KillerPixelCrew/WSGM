# Host and debugging

## Host topology

```text
Program package-cardinality preflight
  -> ShellSession
  -> DeviceCoordinator (machine owner and cycle orchestration)
  -> PluginPackageLoader + collectible PluginLoadContext
  -> DevicePluginRuntime -> IDevicePlugin
  -> host adapter publications
  -> capability/settings/OEM/controller/glyph consumers
```

Exactly zero or one immediate package root is allowed. Zero leaves device-independent WSGM usable;
more than one refuses startup before normal UI/plugin execution. Full validation and loading happen
only when Device Integration is enabled.

Important owners:

- `Global\WSGM.DevicePackageSlot` is a real thread-owned mutex with a five-second acquisition
  budget. Startup inventory, discovery, maintenance, setup, and uninstall use it.
- `Global\WSGM.DeviceOwner` is an unowned named-mutex marker: `createdNew` elects the owner and the
  handle lifetime holds the claim. Never wait on or release it as a thread-owned mutex.
- `Local\WSGM.Shell` admits one shell session.

The coordinator keeps the device-owner marker while the shell runs even if Device Integration is
disabled. Close WSGM before package maintenance; toggling integration off is not owner release.

## Package and load boundary

The protected package root is `%ProgramFiles%\WSGM\DevicePlugins\installed\<package-id>`, with
transactional `.staging` and `.previous` siblings. Validation rejects links/reparse points, unsafe
paths, limit violations, an API mismatch, non-x64 entry code, and invalid entry type/package ID.

The loader streams the entry assembly and resolves package dependencies through
`AssemblyDependencyResolver`. `WSGM.Device.Sdk`, `WinRT.Runtime`, and `Microsoft.Windows.SDK.NET`
bind host-first by simple name; a second WinRT pair can break process-global CsWinRT initialization.
Other managed dependencies prefer a compatible default-context assembly, then the package; native
dependencies are package-confined.

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

Full teardown is best-effort and evidence-bearing: close admission, enter `Deactivating`, perform
controller make-safe, call plugin stop, detach/withdraw publications, dispose, and enter `Disabled`
only when cleanup evidence allows. Accumulate failures so one bad subscriber cannot skip later
restoration. During application shutdown, AutoTDP must stop before the coordinator because its
restore still needs the capability path.

## Publication and command behavior

- Adapter calls execute synchronously on the plugin's publishing thread. The adapter catches
  consumer exceptions and logs `device-plugin-publication-<channel>`; a returned publish call is not
  proof that the production router accepted the record.
- Production validates cycle/descriptor generation, complete descriptor layout, IDs, value shapes,
  bounds, sequence, timestamps, and freshness. Typical freshness is five seconds for telemetry/fan
  RPM, five minutes for charge/lighting, and thirty seconds otherwise.
- Capability lanes serialize per capability key; a plugin must also serialize a transport shared by
  different capability keys.
- Caller timeout can return `TimedOut` or `Indeterminate` while the device call finishes later. The
  host reconciles a late result only when runtime, command ID, and both generations still match.
- User-origin writes may update desired state and pause AutoTDP; automatic and profile-restore
  origins have different persistence behavior. Preserve the origin.

## Evidence ladder

Start at `%LOCALAPPDATA%\WSGM\wsgm.log` for the exact failing run:

1. Record WSGM/package versions, machine, scenario, settings, and a narrow timestamp range.
2. Find `Device plugin startup inventory:` and any gate/cardinality refusal.
3. Separate machine-owner denial from integration-disabled/passive state.
4. Follow `Device cycle: state=...`, cycle generation, and matched/cleared definition.
5. Inspect loader/start errors for API, architecture, entry type, package ID, dependency, and WinRT
   failures.
6. Inspect `plugin/<scope>:` and the precise availability reason.
7. Look for `Device descriptor set rejected`, `Device capability state rejected`,
   `Device capability delta rejected reason=OutOfOrder`, publication-consumer failures, and settings
   manifest refusal/delivery errors.
8. Follow `Device command: capability=..., outcome=..., rollback=...` and any late-result
   reconciled/ignored line.
9. Follow restart count, make-safe phases, plugin stop, and incomplete-cleanup evidence.
10. For input, check HidHide readability/ledger, target generation, stale samples, haptic ownership,
    and explicit stop.

The Settings diagnostics pipe `WSGM.DeviceCoordinator.<sessionId>` is a read-only summary of
package, generation, cycle, and capability counts. It does not expose the complete
detection/lifecycle reason. A passive no-definition result often needs a Device Lab identity
comparison.

`DetectAsync` runs before the plugin can install `PluginTrace`, so a no-match reason cannot rely on
normal plugin trace output.

## Current code-inspection traps

Recheck these paths before attributing these symptoms to plugin code; they were not protected by a
focused regression test when this skill was authored:

- `PluginSettingsCoordinator.Attach` occurs after `DevicePluginRuntime.StartAsync` returns, while a
  plugin commonly publishes its settings manifest inside `StartAsync`. A missing initial settings UI
  can therefore be host subscription ordering rather than a missing plugin publication.
- A detection no-match returns before `_pluginStartAttempted` becomes true, while later full
  coordinator teardown can still request controller release. The runtime lifecycle guard can turn an
  untouched no-match into unverified release noise.
- On resume and controller-management re-enable, `DevicePluginRuntime` advances its adapter cycle
  before invoking the plugin, but `DeviceCoordinator` can update `DeviceCapabilityRouter` only after
  that lifecycle call returns. Otherwise-correct descriptors/states published during the call can
  therefore be rejected against the router's old cycle. Check coordinator, runtime, and router
  generation order before blaming descriptor-before-state logic.

Also check these proven regression patterns: duplicate SDK/WinRT loading, HidHide hiding discovery,
DOS/NT path duplication, state published before fresh-generation descriptors, whole-set omission,
failed haptic writes cached as success, uncertain writes retried, TestKit passing while production
rejects, and high-rate trace spam hiding the first transition.

## File routes

| Concern                       | Start here                                                                                                                               |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------- |
| Host mechanism and rationale  | `docs/device-plugin-system.md`, `docs/device-integration.md`, `docs/device-security.md`                                                  |
| SDK contract                  | `external/WSGM.Device.Sdk/docs/reference.md`, `src/WSGM.Device.Sdk/`                                                                     |
| Package preflight/maintenance | `src/WSGM/Program.cs`, `Core/DevicePackagePolicy.cs`, `DevicePackageSlotGate.cs`, `DevicePackageStager.cs`                               |
| Load and lifecycle            | `Shell/DeviceCoordinator.cs`, `DevicePluginRuntime.cs`, `PluginPackageLoader.cs`                                                         |
| Publications and commands     | `Shell/DeviceCapabilityRouter.cs`, `PluginSettingsCoordinator.cs`, `DeviceOemActionRouter.cs`                                            |
| Controller safety             | `Shell/ControllerManager.cs`, `ControllerMakeSafe.cs`, `HidHideOwnership.cs`, `PluginHapticSink.cs`                                      |
| Target input/output           | `Input/ManagedControllerRouter.cs`, `ViiperControllerBackend.cs`, target report encoders                                                 |
| Host consumers                | `Shell/DeviceOverlayBridge.cs`, `DeviceProfileApplier.cs`, `AutoTdpService.cs`; `Core/DeviceConfiguration.cs`, `PhysicalGlyphCatalog.cs` |
| Reference capability codecs   | `external/WSGM.Device.Msi.Claw8A2Vm/src/WSGM.Device.Msi.Claw8A2Vm/ClawCapabilities.cs`                                                   |
| Reference services/lifecycle  | `ClawResources.cs`, `Claw8A2VmPlugin.cs`, `ClawRecoveryJournal.cs`, `MsiWmiPlatform.cs`                                                  |
| Reference plugin tests        | `external/WSGM.Device.Msi.Claw8A2Vm/tests/WSGM.Device.Msi.Claw8A2Vm.Tests/ClawPluginTests.cs`                                            |

Use focused WSGM tests for package policy, runtime, coordinator concurrency, capability router,
integration-off, controller make-safe, HidHide, settings, desired state, profiles, OEM policy,
glyphs, and plugin trace. Then run the standalone SDK/plugin/Device Lab suites and the root gate.
