# Common plugin contracts

`src/WSGM.Plugin.Sdk` is the MIT, dependency-free common contract assembly. `WSGM.Device.Sdk`
continues to define hardware detection, controllers, capabilities and Device Lab integration.
The existing device runtime still loads the Device SDK directly; common hosting is introduced through
an adapter, not by rewriting hardware contracts in the first slice.

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
resume and preserves an unconfirmed stop result across repeated requests. Its caller serializes
lifecycle operations. The runtime retains its admitted private state directory; the adapter does not
relocate device state. The adapter is covered through a collectible fixture package; production
coordinator admission through the common host is the next integration step.

The initial execution model remains trusted in-process code. Collectible load contexts isolate
dependencies, not security or crashes. A process boundary would require separately designed and
validated transport, permission and recovery contracts. The SDK neither resurrects the retired
DeviceHost protocol nor describes declared permissions as enforced isolation.

The migration follows common contracts, Device compatibility adapter, lifecycle/configuration/events,
action/UI contributions, then an independent non-device consumer. Delivery status lives only in
`_plan/implementation-todo.md`; existing device behavior stays the baseline throughout.
