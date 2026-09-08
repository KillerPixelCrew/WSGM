# Common plugin contracts

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
coexistence without a Device Plugin; external package discovery remains a subsequent migration slice.

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
plugins cannot inject UI code. Rendering and pinning consume these contracts in later slices.

Stop closes action admission immediately and cooperatively cancels the active lifecycle/action call.
The stop and disposal operations still wait behind that call's actual completion, so cancellation
cannot unload code that is still using external resources. Stop tolerates partially completed startup.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

The migration follows common contracts, Device compatibility adapter, lifecycle/configuration/events,
action/UI contributions, then an independent non-device consumer. Delivery status lives only in
`_plan/implementation-todo.md`; existing device behavior stays the baseline throughout.
