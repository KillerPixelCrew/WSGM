# Common plugin contracts

`src/WSGM.Plugin.Ir` is the first hardware-backed independent integration under development (#52).
It owns its USB protocol, command library and companion firmware. Its initial declarative Tools
contributions use the existing common host. Command selection, naming, relearning, timing and scene
management are available through host-rendered action forms. Hardware acceptance remains incomplete.
See its README for the implemented boundary and current limitations.

`src/WSGM.Plugin.Sdk` is the MIT, dependency-free common contract assembly. `WSGM.Device.Sdk`
continues to define hardware detection, controllers, capabilities and Device Lab integration.
The resident Shell session owns the common host. Its Device coordinator admits the existing
Device runtime through an adapter, preserving the Device SDK hardware contracts.

Categories are stable strings. The host owns category policy: Device permits zero or one selected
active instance, while independent categories can permit multiple instances. No Device Plugin is
required on a desktop. Plugin manifests cannot grant themselves multiplicity or privileges.

The common manifest names the assembly, entry type, numeric package version, accepted API range,
dependencies and declared access requirements. Parsing is bounded and rejects unknown members.
The host must copy admitted metadata before asynchronous use, validate paths and dependencies,
and keep plugin instances tied to their admitted identity and generation.

The lifecycle is Start, resident Desktop/Game transitions, Suspend/Resume, Stop, Dispose. Publications carry instance
and generation; the host rejects stale publications. Timeout only cancels waiting and requests
cooperative unwind. It does not establish that plugin code stopped or a hardware write was undone.

`DevicePluginCompatibilityAdapter` wraps the existing device runtime for this lifecycle. It retains
device command and hardware ownership, maps device health, advances the runtime generation on
resume and preserves an unconfirmed stop result across repeated requests. The common host serializes
each instance's lifecycle separately. The Device coordinator keeps controller neutralization and
release before plugin stop. The runtime retains its admitted private state directory; the adapter does
not relocate device state. A collectible fixture exercises the full host/adapter/runtime lifecycle.

Admission reserves both instance identity and category capacity until confirmed stop and successful
disposal. Failed or uncertain release keeps the slot reserved. A timed-out in-process call retains
its lifecycle lane, cancellation budget and instance until its task actually ends; disposal cannot
overtake it. No failed stop or disposal is automatically retried. Trusted code that ignores
cancellation can therefore require process exit to recover its slot.

Health callbacks are checked against their owning registration and generation, then dispatched to
the UI with another generation check. Desktop/Game intent uses increasing revisions, cancels obsolete
cooperative mode work and leaves the Device integration resident. Independent fake instances validate
coexistence without a Device Plugin. Installed packages use the catalog and instance manager described below.

## Configuration and state

`IConfigurablePlugin` declares bounded boolean, numeric or text preferences for plugin behavior.
The host snapshots and validates the schema before startup. It restores saved preferences with
unsaved declaration fallbacks, then delivers a complete immutable `PluginConfiguration` snapshot.
External-state controls belong to action/capability surfaces, not this preferences contract.

An explicit edit includes the revision the UI read. `CommonPluginSettings` validates the change,
persists only those changed keys through `ConfigStore.Mutate`, then dispatches the complete requested
configuration. A stale revision or failed save prevents dispatch. Defaults are not saved implicitly.
Application failure does not erase desired preferences; a mismatched confirmation remains unconfirmed.
There is no automatic configuration retry. Existing Device settings retain their current adapter path.

`PluginStatePublication` carries instance, lifecycle generation, increasing sequence, origin and
optional configuration/action correlation. It describes effective state only and cannot reach the
configuration store. The host accepts bounded primitive values, retains at most 128 state keys per
instance, rejects reordered/stale observations and checks queued UI events again before dispatch.
These are ordinary UI/status events; high-rate controller samples retain their specialized path.

## Named actions and UI contributions

`IPluginActions` declares stable operation names and primitive argument schemas. The host snapshots
them before startup and gives each invocation a fresh operation identity, current generation, origin
and deadline. Stale generations and invalid arguments cannot dispatch. A missing, failed or mismatched
reply remains unconfirmed; there is no automatic retry. `Dispatched` means a command was sent,
whereas `AppliedVerified` requires independent evidence of the declared effect. Route orchestration
must not treat an IR endpoint acknowledgment as proof that a television changed input.

`IPluginUi` supplies bounded status, button, toggle and slider descriptions. Admission checks every
action/argument link and requires numeric bounds for sliders. WSGM owns actual controls and placement;
plugins cannot inject UI code. The overlay Tools page renders common contributions, grouped by
instance and contribution category. Status readback is separate from an editable draft; toggles and
sliders require an explicit Apply press. Refresh never invokes an action. Pinning follows in #53.
Action contributions expose their declared arguments in collapsible forms. Text and numeric fields
use press-to-edit buttons and the Overlay keyboard, so controller navigation never depends on focusing
a bare TextBox. Drafts remain separate from readback and are sent only on explicit invocation.

Stop closes action admission immediately and cooperatively cancels the active lifecycle/action call.
The stop and disposal operations still wait behind that call's actual completion, so cancellation
cannot unload code that is still using external resources. Stop tolerates partially completed startup.

## Package loading and dependencies

`CommonPluginPackage` reads bounded `plugin.wsgm.json` metadata and loads a public parameterless
`IPlugin` entry type with a matching ID. It rejects Device-category packages, which retain their
selected installation slot and adapter. Package roots and entry/manifest files cannot be reparse
points. Package constructors must not acquire external resources.

The common loader reuses the existing `PluginLoadContext`, including host-owned Device/common SDK
type identity, shared WinRT process state, host-first dependencies and collectible package-local
fallbacks. A failed plugin disposal does not explicitly unload its context. The caller must retain
ownership of loading tasks that ignore cancellation.

`CommonPluginDependencyPlan` orders enabled packages before their consumers. Missing, duplicate,
incompatible and cyclic dependencies reject affected packages while preserving independent ones.
Dotted numeric versions compare with omitted build/revision components treated as zero.

A temporary non-device package fixture exercises actual collectible loading, configuration, a named
action with file readback, declarative contributions, resident mode changes and cleanup.

`CommonPluginCatalog` reads `%ProgramFiles%\WSGM\Plugins\<plugin-id>` without executing code.
The directory name must match the manifest ID and the entry assembly must exist. Discovery is
independent of Device Integration and does not enable a package. `AppConfig.PluginInstances` contains
explicit `PluginId`, `InstanceId` and `Enabled` choices; its default is empty.

`CommonPluginManager` starts selected instances in dependency order and retains them across resident
mode changes. Config reload applies only a newly saved preference revision; an unconfirmed revision
is not automatically retried. Disable and shutdown cancel startup, await actual completion and dispose
in reverse admission order. Failed cleanup retains ownership and prevents replacement. Suspend/resume
is deduplicated per instance independently from the Device coordinator. Instance state directories
use a hash of the instance ID to avoid path aliases.

Activation requests carry increasing host revisions. Disabling an instance cancels its pending load
immediately; an obsolete enable cannot start it later. A rapid explicit re-enable waits for confirmed
cleanup before creating the replacement.

Install or replace trusted packages only while their instances are stopped. Settings' Plugin tab
discovers metadata without loading code and exposes activation for installed and configured instances.
Save merges only edited instance choices into a fresh configuration. A package with no configured
instances offers a disabled `default` instance. Additional stable instance IDs can be configured in
`PluginInstances`; every configured instance appears separately. Missing packages remain visible so
their activation can be disabled without discarding preferences.

Loading inherits the application's current
authority; this host neither elevates itself nor grants access based on manifest declarations.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

## Authoring and packaging

From a source checkout, create a new output directory with a harmless common plugin example:

```powershell
.\eng\new-plugin.ps1 -Id example.counter -Output C:\work\CounterPlugin
.\eng\package-plugin.ps1 -Project C:\work\CounterPlugin\Plugin.csproj -Archive C:\work\counter-0.1.0.zip
```

The template references this checkout's MIT common SDK and demonstrates lifecycle, effective state,
a named action and declarative status/button contributions. `-Category wsgm.infrared` or another
stable category changes metadata without introducing a Core specialization. Device packages keep
the existing Device Lab scaffold, validation and hardware harness.

Packaging runs the project's build, checks essential manifest/output fields and creates a new ZIP.
It does not execute the plugin entry type, install, enable or replace a package. Full common manifest,
dependency and UI/action validation remains authoritative in the host. Trust build inputs before
publishing; MSBuild is executable code. Extract an approved archive into the protected
`%ProgramFiles%\WSGM\Plugins\<plugin-id>` directory while the instance is stopped, then enable it
in Settings. Updating follows explicit disable, confirmed cleanup, replacement and re-enable.

The existing `CommonPluginPackageTests` fixture is an offline example harness covering configuration,
actions, state and lifecycle without external hardware. Use the same contract pattern for provider
fakes. Runtime health and action outcomes appear on Tools; a failed or unconfirmed operation is never
an automatic retry request. Common preferences use `PluginConfigurations` with explicit increasing
revisions; provider schema validation occurs before delivery. Their generic Settings editor is not
part of this initial surface; the Device settings editor remains available.

The migration follows common contracts, Device compatibility adapter, lifecycle/configuration/events,
action/UI contributions, then an independent non-device consumer. Delivery status lives only in
`_plan/implementation-todo.md`; existing device behavior stays the baseline throughout.

### Widget declarations

IPluginUi.Widgets is an optional additive declaration surface. The host admits at most 32 widgets,
validates stable IDs and one to eight distinct existing contribution links, and copies plugin-owned
lists. Navigation categories must exist. State predicates reference normal effective-state keys;
missing predicate state means unavailable. Widget rendering and persistent front-page pinning are
tracked as in-progress work under #53.

Widget pin persistence stores plugin ID, configured instance ID and widget ID separately in
AppConfig.PluginWidgetPins. Order follows the list. Normalization removes malformed/duplicate entries,
bounds the list to 64 and retains unavailable providers. Reset order sorts by plugin, instance and
widget identity without removing pins. UI-facing mutations use the normal atomic ConfigStore path.

The plugin source panel now exposes Pin widget and Unpin widget actions for each validated widget
declaration. These edit preferences only on an explicit click and report persistence failures.
Front-page rendering and order controls remain in progress under #53.
