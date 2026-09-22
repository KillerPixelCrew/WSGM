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
- WSGM assembles `DeviceDiagnosticsSnapshot`.
- WSGM wraps accepted observations as `CapabilityStateDelta`.
- `VirtualTargetNeutralized` and `WsgmStateRemoved` are WSGM-owned handoff steps.

## Lifecycle contract

| Call                           | Required behavior                                                                                                               |
| ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------- |
| `DetectAsync`                  | Compare the normalized snapshot against exact supported identity. Acquire nothing mutable.                                      |
| `StartAsync`                   | Install tracing first, recheck identity/firmware, acquire services, publish complete sets and initial state, unwind on failure. |
| `ApplySettingsAsync`           | Receive all declared preferences as a full set. This is not a concealed hardware-write channel.                                 |
| `ExecuteCommandAsync`          | Revalidate everything at the last responsible moment; serialize the actual transport; return a truthful result.                 |
| `SuspendAsync`                 | Stop new work, sampling, and output within the deadline; quiesce or close handles.                                              |
| `ResumeAsync`                  | Reacquire under the new cycle generation and republish descriptors before states.                                               |
| `SetControllerManagementAsync` | Acquire or release only the physical-controller part while the cycle continues. Enabling uses a fresh generation.               |
| `ReleaseControllerAsync`       | Stop acquisition, restore the original mode, verify re-enumeration by physical location, and report the furthest reached step.  |
| `StopAsync`                    | Restore every temporary change, release resources, and report `Clean`, `Unverified`, or `Failed` honestly.                      |
| `DisposeAsync`                 | Last-chance release; never throw.                                                                                               |

Lifecycle calls are serialized, but plugin-started services, commands, and haptic frames can
overlap. Protect shared transports in the plugin. Cancellation after partial acquisition still
requires unwind; cancellation is not rollback.

## Generations and replacement sets

- The host owns `CycleGeneration` and advances it on start, resume, and controller reacquisition.
  Handles, samples, publications, and commands belong to the generation under which they were
  created.
- The plugin owns descriptor generation. It is strictly increasing whenever a descriptor/layout
  changes within one cycle; the adapter resets its view when the host advances the cycle, so a
  plugin may publish generation 1 again after resume or controller reacquisition.
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
- Both limits are readable, writable Integer Watt limits with no `InstanceId`, and `Minimum` and
  `Step` are both greater than zero. Validate with `DevicePowerPair.TryValidate`.
- `ApplyPowerPair` asks the plugin to write, read back and roll back both limits together.
  `ReadbackValue` then reports the sustained wattage.

The SDK XML docs say ordinary commands keep independent-limit behavior. The Claw departs from that
to protect a firmware invariant. It keeps PL1 <= PL2 by carrying the other limit along when a
single-limit write would break the rule (see the Claw `AGENTS.md`). If another plugin needs the same
carry, apply the requested value exactly and publish both resulting states. Raise the SDK doc and
Claw mismatch with the maintainer rather than copying either one silently.

`CommandOutcome` means:

- `Accepted`: admitted but not yet a claim of hardware effect.
- `AppliedUnverified`: the write returned without independent readback.
- `AppliedVerified`: independent readback exists and matches; include `ReadbackValue`.
- `Rejected`: nothing was attempted.
- `TimedOut` or `Indeterminate`: the effect is unknown. Never blindly retry a persistent write.

Capture the original value immediately before the first mutation and preserve that first original
through retries or reopen. Restore it on failure/stop where policy requires it. Report rollback and
device-persistent uncertainty rather than converting it into success.

State quality steers host automation. WSGM re-applies a desired value automatically only when the
published `HardwareStateQuality` is `Observed` or `Verified`, no command is pending, and the
previous result was not `TimedOut` or `Indeterminate`
(`src/WSGM/Shell/DeviceDesiredWriteAdmission.cs`). Publishing an `Unknown` or any other quality
therefore turns off restoration for that capability.

## Controller, OEM, and haptics

- Plugins publish device-owned physical interfaces and whether WSGM must hide them. Plugins never
  call VIIPER, manipulate WSGM's Steam Input lease, or edit HidHide.
- Topology continuation uses the physical location path because a mode switch can change product id,
  expose no container id, and expose a serial in only one mode. A location path is diagnostic and
  continuation identity, not a package match predicate.
- WSGM neutralizes and stops forwarding before the plugin releases. The physical device remains
  hidden until acquisition stops and the original mode is restored; otherwise the game sees two
  controllers.
- `HapticOutputFrame.TargetGeneration` is independent of the device-cycle generation. WSGM drops
  frames whose target generation does not match the live target before delivery. No plugin API
  exposes the current target generation, and the Claw does not read it. The plugin clamps to its
  declared channels with `HapticCapabilities.Clamp` and drops unsupported channels without
  redistribution. For zero output it uses `HapticOutputFrame.Stop(targetGeneration, timestamp)`.
  `docs/reference.md` still tells plugins to drop non-current frames themselves; doing so is
  harmless but redundant.
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
