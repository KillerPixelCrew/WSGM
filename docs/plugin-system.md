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
coexistence without a Device Plugin; external package discovery and configuration remain subsequent
migration slices.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

The migration follows common contracts, Device compatibility adapter, lifecycle/configuration/events,
action/UI contributions, then an independent non-device consumer. Delivery status lives only in
`_plan/implementation-todo.md`; existing device behavior stays the baseline throughout.
