# Contract and ownership

## Runtime direction

The boundary is typed and in-process; it is not a JSON RPC protocol.

```text
WSGM host -> IDevicePlugin
  Detect, Start, ApplySettings, ExecuteCommand, Suspend, Resume,
  ApplyHapticOutput, SetControllerManagement, ReleaseController,
  GetDiagnostics, Stop, Dispose

plugin -> IPluginHostAdapter
  descriptors, capability state, physical devices and haptics,
  full controller samples, OEM controls and events, settings manifest,
  traces and background faults
```

Do not confuse host-owned types with plugin publications:

- `DeviceCycleState` is host lifecycle state; start/resume return `PluginOperationalState`.
- WSGM wraps accepted observations as `CapabilityStateDelta`.
- Neutralizing and removing the virtual target and WSGM's HidHide entries are WSGM's half of a
  controller release; `HandoffScope` only tells the plugin whether the cycle continues.

## Lifecycle contract

| Call                           | Required behavior                                                                                                               |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------- |
| `DetectAsync`                  | Compare the normalized snapshot against exact supported identity. Acquire nothing mutable.                                      |
| `StartAsync`                   | Install tracing first, recheck identity/firmware, acquire services, publish complete sets and initial state, unwind on failure. |
| `ApplySettingsAsync`           | Receive all declared preferences as a full set. This is not a concealed hardware-write channel.                                 |
| `ExecuteCommandAsync`          | Revalidate everything at the last responsible moment; serialize the actual transport; return a truthful result.                 |
| `SuspendAsync`                 | Stop new work, sampling, and output within the deadline; quiesce or close handles.                                              |
| `ResumeAsync`                  | Reacquire under the new cycle generation and republish descriptors before states.                                               |
| `SetControllerManagementAsync` | Acquire or release only the physical-controller part while the cycle continues, in the same generation.                         |
| `ReleaseControllerAsync`       | Best effort: stop acquisition, write the original mode back, log what failed, and return. Nothing is reported or read back.     |
| `StopAsync`                    | Restore every temporary change, release resources, and report `Clean`, `Unverified`, or `Failed` honestly.                      |
| `DisposeAsync`                 | Last-chance release; never throw.                                                                                               |

Lifecycle calls are serialized, but plugin-started services, commands, and haptic frames can
overlap. Protect shared transports in the plugin. Cancellation after partial acquisition still
requires unwind; cancellation is not rollback.

## Generations and replacement sets

- The host owns `CycleGeneration` and advances it on start and resume. Descriptor sets, capability
  states and commands carry it. Controller samples, OEM events, haptic frames and
  `PluginControllerManagementContext` carry none: turning the controller on is not a new cycle.
- The plugin owns descriptor generation. It is strictly increasing whenever a descriptor/layout
  changes within one cycle; the adapter resets its view when the host advances the cycle, so a
  plugin may publish generation 1 again after resume.
- A corrected descriptor set after a rejection needs a newer descriptor generation. The adapter can
  advance before the production router rejects content, so reusing the rejected number can strand
  later states against different host/router views.
- Descriptors, physical-device identities, OEM controls and the settings manifest are whole-set
  replacements. Omitting an item withdraws it. If a new settings manifest fails validation, the host
  keeps the previous one.
- The descriptor set carries its sections, categories and placement. It also carries the API 5
  layout hints: `Prominence` (Normal, Primary or Compact) and `LayoutPair`, which must name a
  different descriptor in the same explicit section and category. It carries power presets and the
  power pair too. The production router checks `CapabilityLayout.TryValidate`,
  `DevicePowerPreset.TryValidate` and `DevicePowerPair.TryValidate` and rejects the whole set if any
  one fails.
- Capability states are observations, not desired values, progress, or command acknowledgements.
- Controller samples are complete latest-wins state. Never publish deltas, carry stale buttons
  forward, or synthesize missing controls or motion.

## Commands and state truth

The normal host preflight checks attachment, descriptor/state freshness, availability, value shape,
bounds, and power-source policy. The plugin must still recheck identity, firmware, range, resource
state, both generations, and deadline immediately before touching hardware.

The optional sustained/boost pair works like this:

- `PairedPowerLimitId` goes on a `PowerSustainedLimit` descriptor and names exactly one
  `PowerSlowLimit` peer.
- Both limits are writable Integer Watt limits with no `InstanceId` (readable or not), and `Minimum`
  and `Step` are both greater than zero. Validate with `DevicePowerPair.TryValidate`.
- WSGM decides both limits. Every write to either one carries the other in
  `CapabilityCommand.PairedPowerLimitWatts`, the sustained limit never above the boost limit
  (`DeviceCapabilityRouter.PairedWatts`).
- The plugin resolves the pair with `DevicePowerPair.TryResolve`, which refuses a missing or
  out-of-range companion and an inverted pair, and writes both values as given. It owns the write
  order and any readback, never the relationship: do not carry one limit along with the other in a
  package. A verified result's `ReadbackValue` reports the commanded limit.

`CommandOutcome` means:

- `Accepted`: admitted but not yet a claim of hardware effect.
- `AppliedUnverified`: the write returned without a matching readback. This is success: publish the
  written value as observed. Never gate a write on readback or turn a missing or different readback
  into a failure or rollback.
- `AppliedVerified`: independent readback exists and matches; include `ReadbackValue`.
- `Rejected`: nothing was attempted.
- `TimedOut` or `Indeterminate`: the effect is unknown. Never blindly retry a persistent write.

Capture the original before the first temporary mutation when it can be read, and preserve that
first original through reopen and explicit later commands. Journal before writing; restore under the
package's release/recovery policy. The first-party packages do not immediately roll back an
uncertain power/fan write. Readback is optional evidence, not a prerequisite for a supported write.

State quality steers host automation only through `Stale` and `Faulted`. WSGM re-applies a desired
value automatically whenever the capability is available, its state is neither stale nor faulted,
the observed value differs and no command is pending
(`src/WSGM/Shell/DeviceDesiredWriteAdmission.cs`). A value that was never read back (`Unknown`) is
still restored. After a `TimedOut` or `Indeterminate` result the same value is not written again
automatically; a different desired value is a new write and goes ahead, with no readback awaited.

## Controller, OEM, and haptics

- Plugins publish device-owned physical interfaces and whether WSGM must hide them. Plugins never
  call VIIPER, manipulate WSGM's Steam Input lease, or edit HidHide.
- A location path is diagnostic and continuation identity across a mode switch (which can change the
  product id, expose no container id, and expose a serial in only one mode), not a package match
  predicate.
- WSGM neutralizes and stops forwarding before the plugin releases. The physical device remains
  hidden until the plugin has let go and WSGM has removed its target; otherwise the game sees two
  controllers. `ReleaseControllerAsync` is best effort and returns nothing, and WSGM runs its own
  removal whatever the plugin did.
- A pad that is not present at acquire, or drops off the bus while read, is a state, not a fault.
  Report the controller service Degraded and wait with `DeviceReconnect` (every half second until
  the device is back), then attach in the same cycle. The reattach runs once each time the device
  returns; one that throws ends the wait and faults the controller until a user action (controller
  management off and on), so its writes are never repeated. This is how both first-party packages
  survive a wake.
- The host sends no motion-demand signal. Current Claw/Ally motion services run with the device
  cycle and controller-management toggles change only controller ownership. Keep one
  `MotionSampleBuilder` for the device's life so a restarted stream keeps its measured zero-rate
  offset.
- A `HapticOutputFrame` is a readonly record struct holding the whole motor state, with no target or
  generation: a newer frame replaces an older one, and the path from the virtual target allocates
  nothing per frame. The plugin clamps to its declared channels with `HapticCapabilities.Clamp` and
  drops unsupported channels without redistribution. For zero output it uses
  `HapticOutputFrame.Stop(timestamp)`.
- `OemControlEvent.DeduplicationId` lets the host collapse the same physical press observed through
  more than one source. Do not turn a keyboard side effect into the primary hardware identity.

## Diagnostics and serialization

- `PluginTrace.Install(context.Host)` should be the first `StartAsync` operation.
- Use `Change(scope, key, message)` for polled transitions; `Debug` is opt-in detail, not permission
  to format a message at 100-125 Hz.
- `ReportFault` is for plugin-owned background work that fails after its initiating call returned,
  not for an ordinary synchronous command or lifecycle exception.
- Trace delivery is best effort and unordered with publications; behavior must not depend on it.
- `DeviceJsonContext` currently covers only `PluginManifest` and `GlyphProfileManifest`. Runtime
  lifecycle/publication objects cross as normal typed calls, not generic serialized messages.
- Only enums carrying their explicit converter are string-serialized. Inspect the type instead of
  assuming a repository-wide rule.

## Test adapter limits

`TestPluginHostAdapter` records descriptor sets, states, physical-device sets, the latest haptic
declaration, controller samples, OEM sets/events, settings manifests, traces, and keyed `Changes`.
`ReportFault` appears as an Error trace. It checks nulls and cancellation but intentionally does not
reproduce production generation, descriptor, state, range, freshness, or router validation. Assert
SDK `TryValidate` methods directly and add a WSGM host test when acceptance by production matters.

## Retired architecture that must stay retired

Do not revive deleted `Ipc/*`, codecs, control pipes, frame streams, shared rings, wire messages,
generic lifecycle JSON, `Authoring/*`, `CapabilityRegistry`, or `PluginResourceCoordinator`. WSGM's
current runtime is one self-contained managed process with a collectible in-process plugin.
