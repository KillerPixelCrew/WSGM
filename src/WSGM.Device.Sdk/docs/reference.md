# WSGM.Device.Sdk reference

The public contract in `WSGM.Device.Sdk`, type by type, with every rule and limit the host applies
to what a plugin publishes. The XML documentation on each member is the authoritative wording; this
document lets the contract be read as a whole, in the order a plugin experiences it. How WSGM hosts
a plugin (discovery, the package slot, the load context, deadlines, the overlay, profiles and the
controller path) is not covered here.

Related:

- WSGM `docs/device-plugin-system.md`: the host mechanism.
- WSGM `docs/device-plugin-authoring.md`: building, testing, packing and installing a package.
- [Device Lab](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.DeviceLab): the
  authoring tool.

| Fact               | Value                                                                             |
| ------------------ | --------------------------------------------------------------------------------- |
| Assembly / package | `WSGM.Device.Sdk`                                                                 |
| Target framework   | `net10.0-windows`, matching the host that loads the plugin                        |
| Dependencies       | none; a plugin inherits nothing from the SDK                                      |
| API version        | `DeviceApi.Version = 3`; WSGM, Device Lab and every plugin require an exact match |
| Package version    | `0.1.0`; pre-1.0, a breaking change moves the minor version                       |
| Licence            | MIT (WSGM itself is GPL-3.0-or-later)                                             |
| Documentation      | every public member is documented; an undocumented member fails the build         |

## The contract at a glance

A plugin is one class implementing `IDevicePlugin`, shipped with a six-field `plugin.wsgm.json`.
WSGM loads it in-process and drives it through one lifecycle per WSGM run. Everything crossing the
boundary is a semantic record: no transport, handle, path, script or UI travels in either direction.

```text
 WSGM ──────────────────────────────────────────────────────────────► plugin
   DetectAsync(PluginDetectionContext)                exact identity match, no side effects
   StartAsync(PluginStartContext)                     one cycle begins; host adapter handed over
   ApplySettingsAsync(values)                         every declared setting, as a full set
   ExecuteCommandAsync(CapabilityCommand)             one semantic write or action
   ApplyHapticOutputAsync(HapticOutputFrame)          virtual-target output → physical motors
   SetControllerManagementAsync(...)                  controller ownership on/off inside a cycle
   SuspendAsync / ResumeAsync                         quiesce for sleep/lock, revalidate after
   ReleaseControllerAsync(...)                        make-safe handoff of the physical pad
   GetDiagnosticsAsync()                              bounded key/value facts
   StopAsync(PluginStopContext)                       restore and release everything
   DisposeAsync()                                     last call before the context unloads

 plugin ────────────────────────────────────────────────────────────► WSGM (IPluginHostAdapter)
   PublishDescriptorsAsync(CapabilityDescriptorSet)   what the device can do (whole set)
   PublishCapabilityStateAsync(CapabilityState)       one observed value
   PublishPhysicalDevicesAsync(devices, haptics)      HID interfaces WSGM may hide, motor facts
   PublishControllerSampleAsync(sample)               full pad state at device cadence
   PublishOemControlsAsync(controls)                  the vendor buttons that exist
   PublishOemEventAsync(OemControlEvent)              one press/release of one of them
   PublishSettingsManifestAsync(manifest)             the preferences WSGM should draw and keep
   Trace(level, scope, message)                       one log line; never throws
   ReportFault(scope, message)                        a background service died; cycle is invalid
```

Two integers travel with almost every record so that a stale message can be refused rather than
applied late:

- Cycle generation (`long`), advanced by the host at every start, resume and controller
  reacquisition. Every handle the plugin opens belongs to the generation in force when it was
  opened. A publication or command carrying an old cycle generation is refused.
- Descriptor generation (`long`), owned by the plugin and incremented whenever any descriptor
  changes. A command authored against an older descriptor generation is refused, because the range
  it was validated against no longer exists.

## Lifecycle: `IDevicePlugin`

The entry type named by the manifest. The host constructs it with its public parameterless
constructor; it lives for one WSGM run and is `IAsyncDisposable`. Lifecycle calls are serialized, so
a plugin never sees two at once, but commands and haptic frames can arrive while a background
service the plugin started is running.

The cancellation token passed to a lifecycle call is the host's deadline for that call. A plugin
that ignores it keeps the host waiting until the outer application deadline, after which WSGM
proceeds with its own cleanup and records the plugin's answer as unverified.

| Member                                                                | When the host calls it                                                                                                 | What the plugin does                                                                                                                                                                                                                    |
| --------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PackageId`                                                           | Any time.                                                                                                              | Return the stable id from `plugin.wsgm.json`.                                                                                                                                                                                           |
| `DetectAsync(PluginDetectionContext, ct)`                             | Once per run, before any mutable work; also by Device Lab's `test plugin`.                                             | Compare `context.Identity` with the device definitions it knows. Return `Matched` with a `DeviceDefinitionId`, or `Matched = false` with a `Reason`. Acquire nothing mutable.                                                           |
| `StartAsync(PluginStartContext, ct)`                                  | Once, after a match, when Device Integration is enabled.                                                               | Install `PluginTrace`, open transports, publish the settings manifest, descriptor set, physical devices, OEM controls, then initial state. On cancellation unwind whatever was acquired. Return the aggregate `PluginOperationalState`. |
| `ApplySettingsAsync(values, ct)`                                      | Once after start and on every change, always with every declared setting. Never called when no settings were declared. | Take the validated preferences into account. This is not a hardware-write path. The default implementation is a no-op.                                                                                                                  |
| `ExecuteCommandAsync(CapabilityCommand, ct)`                          | On user intent from the overlay, Settings, the native QAM, profile application, or a Device Lab attended action.       | Revalidate identity, firmware, range and current state, check both generations, apply, read back where possible, return a truthful `CapabilityCommandResult`.                                                                           |
| `SuspendAsync(PluginQuiesceContext, ct)`                              | On sleep or session lock.                                                                                              | Start no long operation, stop sampling and output, hold or close handles; finish before `Deadline`.                                                                                                                                     |
| `ResumeAsync(PluginResumeContext, ct)`                                | After wake or unlock.                                                                                                  | Revalidate identity, reacquire under the new `CycleGeneration`, republish descriptors and state, return the aggregate state.                                                                                                            |
| `GetDiagnosticsAsync(ct)`                                             | For the diagnostics snapshot in the overlay and the log.                                                               | Return bounded key/value facts about services and recovery. No transports, secrets or identifiers.                                                                                                                                      |
| `ApplyHapticOutputAsync(HapticOutputFrame, ct)`                       | Whenever the virtual target emits output.                                                                              | Drive the motors; drop the frame if its `TargetGeneration` is not current. Never trace per frame.                                                                                                                                       |
| `ReleaseControllerAsync(PluginControllerReleaseContext, ct)`          | During the make-safe handoff, controller-only or full deactivation.                                                    | Stop reading, close handles, restore the original controller mode, verify re-enumeration, report the furthest `ControllerHandoffStep` reached and an honest `ControllerHandoffResult`.                                                  |
| `SetControllerManagementAsync(PluginControllerManagementContext, ct)` | When the user toggles controller management while the cycle continues.                                                 | Acquire (under the fresh generation) or release the physical controller only; republish the controller and haptic capability states.                                                                                                    |
| `StopAsync(PluginStopContext, ct)`                                    | At the end of the cycle, for one of the `PluginStopReason` values.                                                     | Restore every temporarily changed hardware state, release everything, report `Clean`, `Unverified` or `Failed` truthfully.                                                                                                              |
| `DisposeAsync()`                                                      | After stop, before the collectible load context unloads.                                                               | Release whatever survived stop. Must not throw.                                                                                                                                                                                         |

### Lifecycle records

| Type                                | Fields                                                                                                                                      | Notes                                                                                                     |
| ----------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `PluginDetectionContext`            | `DeviceIdentitySnapshot Identity`                                                                                                           | Read-only, normalized observations.                                                                       |
| `PluginDetectionResult`             | `bool Matched`, `string? DeviceDefinitionId`, `CapabilityReason? Reason`                                                                    | `DeviceDefinitionId` only when matched.                                                                   |
| `PluginStartContext`                | `IPluginHostAdapter Host`, `long CycleGeneration`, `string DeviceDefinitionId`, `string StateDirectory`, `bool ControllerManagementEnabled` | `StateDirectory` is a private writable directory; the plugin alone owns its files and keeps them bounded. |
| `PluginStartResult`                 | `PluginOperationalState State`, `CapabilityReason? Reason`                                                                                  | Returned by start and resume.                                                                             |
| `PluginOperationalState`            | `Passive`, `Active`, `Degraded`                                                                                                             | Passive: nothing mutable acquired. Degraded: at least one service usable and one unavailable.             |
| `PluginDiagnostics`                 | `IReadOnlyDictionary<string,string> Values`                                                                                                 | Ordinal keys, sanitized values.                                                                           |
| `PluginStopResult`                  | `PluginStopStatus Status`, `CapabilityReason? Reason`                                                                                       |                                                                                                           |
| `PluginStopStatus`                  | `Clean`, `Unverified`, `Failed`                                                                                                             | Unverified: cleanup ran but a restoration could not be confirmed.                                         |
| `PluginQuiesceContext`              | `DateTimeOffset Deadline`                                                                                                                   |                                                                                                           |
| `PluginResumeContext`               | `long CycleGeneration`, `DateTimeOffset Deadline`                                                                                           | New generation for everything reopened.                                                                   |
| `PluginControllerReleaseContext`    | `HandoffScope Scope`, `DateTimeOffset Deadline`                                                                                             |                                                                                                           |
| `PluginControllerManagementContext` | `bool Enabled`, `long CycleGeneration`, `DateTimeOffset Deadline`                                                                           | The fresh generation applies when enabling.                                                               |
| `PluginControllerRelease`           | `ControllerHandoffStep Step`, `ControllerHandoffResult Result`, `IReadOnlyList<PhysicalDeviceIdentity> ReleasedDevices`                     | What was observed after release.                                                                          |
| `PluginStopContext`                 | `PluginStopReason Reason`, `DateTimeOffset Deadline`                                                                                        |                                                                                                           |
| `PluginStopReason`                  | `WsgmExiting`, `IntegrationDisabled`, `Updating`, `SessionEnding`, `Uninstalling`, `StartCanceled`, `StartFailed`, `RuntimeFault`           | `Updating` and `Uninstalling` arrive with the compressed cleanup budget.                                  |

## Publishing: `IPluginHostAdapter`

The publication surface in `PluginStartContext.Host`, valid for the whole cycle. WSGM validates
every publication; an invalid one is refused (logged, previous value kept) rather than partially
applied.

| Member                                                                 | Contract                                                                                                                                                                                                                                                                       |
| ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `long CycleGeneration`                                                 | The generation in force. Stamp it on every sample, state and descriptor set.                                                                                                                                                                                                   |
| `PublishDescriptorsAsync(CapabilityDescriptorSet, ct)`                 | Replaces the whole set. Carries a new `Generation` when anything changed, and the current `CycleGeneration`. Validation rules are under Capabilities.                                                                                                                          |
| `PublishCapabilityStateAsync(CapabilityState, ct)`                     | One observation for one capability instance, stamped with the descriptor generation it was produced against.                                                                                                                                                                   |
| `PublishPhysicalDevicesAsync(devices, HapticCapabilities? output, ct)` | The HID interfaces the plugin owns, whether each must be hidden for controller management, and what the motors can do. `output = null` declares no haptic sink.                                                                                                                |
| `PublishControllerSampleAsync(CanonicalControllerSample, ct)`          | A full pad state. WSGM keeps only the newest accepted sample; a sample with a stale cycle generation is dropped. Never trace here.                                                                                                                                             |
| `PublishOemControlsAsync(controls, ct)`                                | The closed set of vendor controls. WSGM renders them as assignable rows.                                                                                                                                                                                                       |
| `PublishOemEventAsync(OemControlEvent, ct)`                            | One press or release edge, deduplicated by `DeduplicationId`.                                                                                                                                                                                                                  |
| `PublishSettingsManifestAsync(PluginSettingsManifest, ct)`             | A declaration WSGM draws, validates, stores and localizes. A manifest that fails `TryValidate` is refused and the previous one kept.                                                                                                                                           |
| `Trace(DeviceTraceLevel, scope, message)`                              | Synchronous, void, never throws. Best-effort and unordered with respect to publications. Truncated past `PluginTrace.MaxMessageLength`.                                                                                                                                        |
| `TraceChange(DeviceTraceLevel, scope, key, message)`                   | Same contract, plus a key the host uses to suppress an unchanged repeat and count it. Default implementation forwards to `Trace`, so a host built before API 3 writes every call instead of losing it.                                                                         |
| `ReportFault(scope, message)`                                          | Default implementation traces at `Error`. WSGM's adapter also closes command admission, makes the device safe, stops and disposes the plugin, and restarts it under the bounded fault policy. Use only for failures of plugin-started work that outlive their initiating call. |

### `PluginTrace`

A static, ambient sink shaped like WSGM's own `Log`: a no-op until `Install(adapter)` is called,
normally as the first statement of `StartAsync`. `DeviceTraceLevel` is `Info`, `Warn`, `Error`,
`Debug` — declared in that order so the values that existed before `Debug` did not move.

| Member                                      | Behaviour                                                                                                                                              |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `MaxMessageLength = 1024`                   | Longest message WSGM records.                                                                                                                          |
| `Install(IPluginHostAdapter? sink)`         | Routes subsequent traces; `null` silences them.                                                                                                        |
| `Info(scope, message)`                      | A decision or state change on a normal path.                                                                                                           |
| `Warn(scope, message)`                      | Something degraded, was refused or fell back.                                                                                                          |
| `Error(scope, message)`                     | A failure the plugin could not handle.                                                                                                                 |
| `Debug(scope, message)`                     | Detail worth keeping only while investigating. The host suppresses it unless diagnostics are raised.                                                   |
| `Change(scope, key, message, level = Info)` | A polled state under a key, written only when it differs from that key's last line. Repeats are counted, not dropped.                                  |
| `Failure(scope, context, Exception)`        | Writes `Warn` as `context: ExceptionType: message`. Put one at the top of every `catch` that would otherwise collapse distinct failures into one flag. |

`Debug` is not a licence to trace per sample: a suppressed line still costs the call and the string
that built it, and raising diagnostics must not turn the log into the thing the level exists to
prevent. Use `Change` for anything a poll loop observes — one measured device session produced 7,619
motion lines, 40% of everything recorded, from two messages a reader kept re-stating either side of
a freshness threshold.

A trace is swallowed if the sink throws (except `OutOfMemoryException`). Never trace inside the
controller sample loop: it runs at about 125 Hz and would out-write everything else in the log.

## Cycle state and controller handoff

### `DeviceCycleState`

Host-owned, serialized as a string. The cycle spans the whole WSGM run and ends only when WSGM exits
or the user turns Device Integration off. Entering or leaving Game Mode, closing a game, restarting
Steam, toggling controller management and a degraded capability all happen inside one cycle.

| State          | Meaning                                                                                                                                                             |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Disabled`     | Device Integration is off. No runtime, service or hook exists.                                                                                                      |
| `Detected`     | The exact board matched; capabilities are still being probed.                                                                                                       |
| `Passive`      | Hardware exists, but another owner or a missing prerequisite prevents acquiring one or more resources.                                                              |
| `Activating`   | Snapshots and device-service startup are in progress.                                                                                                               |
| `Active`       | At least one capability is owned and healthy.                                                                                                                       |
| `Degraded`     | Some capabilities failed; the healthy ones remain usable.                                                                                                           |
| `Suspended`    | Writes, samples, rumble and hooks are quiesced for sleep or a session transition.                                                                                   |
| `Deactivating` | New commands are refused while owned state is released and restored.                                                                                                |
| `Faulted`      | The runtime failed repeatedly and will not restart automatically. Fails open: the virtual target and WSGM's HidHide entries are removed; desired state is retained. |

### `ControllerHandoffStep`

The shared ordering of the make-safe handoff (string-serialized), so a pasted log settles how far it
got. WSGM neutralizes its virtual target but keeps the physical device hidden until the plugin has
stopped reading and restored the original mode. Un-hiding first would expose a device the plugin
still holds, and Steam and the running game would see both controllers at once.

| Step                         | Owner  | Meaning                                                                                                            |
| ---------------------------- | ------ | ------------------------------------------------------------------------------------------------------------------ |
| `NotStarted`                 | –      | Nothing has started.                                                                                               |
| `VirtualTargetNeutralized`   | WSGM   | A neutral state was sent to the virtual target and forwarding stopped. The physical device stays hidden.           |
| `PhysicalAcquisitionStopped` | plugin | Reading stopped and handles closed.                                                                                |
| `OriginalModeRestored`       | plugin | The controller mode captured at activation was written back.                                                       |
| `TopologyVerified`           | plugin | The expected re-enumeration was seen at the same USB location path.                                                |
| `TopologyUnverified`         | plugin | Re-enumeration could not be confirmed within the budget. Terminal; cleanup continues and the result is unverified. |
| `WsgmStateRemoved`           | WSGM   | The virtual target and only WSGM's own HidHide entries were removed.                                               |

Topology is verified by location path, not identity: a mode change alters the product id, the
container id is the null GUID on the reference hardware, and the USB serial exists in only one mode.

`ControllerHandoffResult`: `InProgress`; `ReleasedVerified` (every step observed; a claim about
WSGM's own state only, never that another manager has taken the device); `ReleasedUnverified`
(cleanup finished with at least one step unconfirmed; journalled for the next start).

`HandoffScope`: `ControllerOnly` (the cycle and every non-controller resource continue, including
the OEM event path) or `FullDeactivation` (WSGM is exiting or Device Integration was turned off).

### `DeviceDiagnosticsSnapshot`

A bounded read-only snapshot the host assembles: `PackageId`, `DeviceId`, `CycleState`,
`CycleGeneration`, `PluginValues` (from `GetDiagnosticsAsync`) and `CapturedAt` (UTC).

## Identity

### `DeviceIdentitySnapshot`

The observed half of identity. Device Lab and WSGM's runtime produce it; the contract fixes which
facts exist and how they compare. Every string arrives already normalized through `IdentityText`.

| Field                                                              | Source                                                                                                    |
| ------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------------- |
| `SystemManufacturer`, `SystemProduct`, `SystemSku`, `SystemFamily` | SMBIOS type 1                                                                                             |
| `BaseboardProduct`, `BaseboardVersion`                             | SMBIOS type 2; `BaseboardProduct` is the exact board identifier                                           |
| `BiosVersion`                                                      | System BIOS                                                                                               |
| `EcFirmwareVersion`                                                | Vendor provider, not SMBIOS                                                                               |
| `McuFirmwareVersion`                                               | Controller or MCU firmware                                                                                |
| `CpuIdentity`                                                      | Normalized `family-model-stepping`                                                                        |
| `UsbEndpoints`                                                     | `IReadOnlyList<UsbEndpointObservation>` present right now                                                 |
| `WmiProviderSignatures`                                            | Presence-only signatures of WMI providers, classes or methods. Enumerability never authorizes invocation. |

`UsbEndpointObservation`: `VendorId` and `ProductId` (four uppercase hex digits), `InterfaceNumber`,
`DeviceRelease` (`bcdDevice`, four uppercase hex digits), `ReportDescriptorHash`, `ReportLengths`,
`LocationPath`. The location path names a port on one machine, so it is diagnostic-only and unusable
as a manifest predicate. It is the continuation key for hotplug and controller mode changes, being
the only identifier verified stable across a full mode switch.

### `IdentityText`

| Member                        | Behaviour                                                                                                                                                                          |
| ----------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Normalize(string?)`          | Trims and collapses internal whitespace runs to one space. Returns `null` for null, empty or whitespace, so "absent" and "blank" compare the same.                                 |
| `Matches(observed, expected)` | Normalizes both and compares ordinally, ignoring case. Two absent values are not a match: a definition gating on EC firmware must not be satisfied by a machine that reports none. |

## Capabilities

### `CapabilityDescriptorSet`

Always published whole. A capability missing from a new set has gone away and its control
disappears; nothing lingers as permanently unavailable.

| Field                                             | Meaning                                                                                                   |
| ------------------------------------------------- | --------------------------------------------------------------------------------------------------------- |
| `long Generation`                                 | Monotonic; increments whenever any descriptor changes.                                                    |
| `long CycleGeneration`                            | The device generation these descriptors describe.                                                         |
| `IReadOnlyList<CapabilitySection> Sections`       | The overlay sections descriptors may reference, in declaration order. Empty uses the predefined sections. |
| `IReadOnlyList<CapabilityDescriptor> Descriptors` | Every capability the device currently offers.                                                             |

### `CapabilitySection` and `CapabilityCategory`

`DeviceSections` predefines Power (`power`), RGB (`rgb`), Controller (`controller`) and Info
(`info`). WSGM and plugins place controls on the same pages using these stable IDs. Descriptors may
reference a predefined section without declaring it. A plugin can declare a record copy with
categories; the predefined identity, title key, icon and ordering stay WSGM-owned. Existing valid
declarations are accepted, with shared metadata canonicalized by the host. Custom sections still
require a declaration. Hosts use `IncludePredefined` when validating and projecting layouts and
render only populated pages. WSGM's Windows energy controls keep Power populated even when device
integration is disabled.

A section is a page of the Device overlay; a category is a heading on that page. Both travel inside
the set so layout and content replace atomically. For custom sections, the plugin chooses placement,
order, a title key and an icon; WSGM owns every string, geometry and control shape.
`TryValidate(out error)` on both types applies exactly these rules.

| `CapabilitySection` field | Rule                                                                                        |
| ------------------------- | ------------------------------------------------------------------------------------------- |
| `SectionId`               | Identifier (`PlainText.IsIdentifier`), at most 64 characters.                               |
| `Key`                     | A `SettingSectionKey` WSGM localizes, or `Custom`.                                          |
| `CustomTitle`             | Required plain text (≤ 48) when `Key` is `Custom`; must be null otherwise.                  |
| `CustomDescription`       | Optional plain text (≤ 96) for the section card; null means WSGM's own wording for the key. |
| `Icon`                    | A `SectionIcon`; the default `None` lets WSGM derive one from the key.                      |
| `SortOrder`               | Placement among sections; ties break on declaration order.                                  |
| `Categories`              | At most 16, unique ids, each validating on its own.                                         |

`CapabilityCategory`: `CategoryId` (identifier ≤ 64), `Key`, `CustomTitle` (≤ 48, same rule as
above), `SortOrder`. Limits: `MaxSections = 16` per set, `MaxCategories = 16` per section.

`SectionIcon`: `None`, `Power`, `Fan`, `Battery`, `Lighting`, `Controller`, `Display`, `Gauge`,
`Wrench`.

### `CapabilityDescriptor`

Immutable. When firmware, endpoints or dependency health change what a capability can do, the plugin
publishes a complete replacement set under a new generation. A descriptor is a description, not a
promise: WSGM validates against it for UI consistency; the plugin revalidates on every command.

| Field                                             | Meaning                                                                                                                 |
| ------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `CapabilityId`                                    | Stable id such as `power.primary-limit`.                                                                                |
| `InstanceId`                                      | Discriminator when a device has several of one capability (two fans).                                                   |
| `Role`                                            | What it means to WSGM (`CapabilityRole`).                                                                               |
| `ValueKind`                                       | Shape of its value (`CapabilityValueKind`).                                                                             |
| `Display`                                         | Its label (`CapabilityDisplay`).                                                                                        |
| `SectionId`                                       | A section declared in the same set, or for a `Generic*` role a settings-manifest section.                               |
| `CategoryId`                                      | A category of that section, or null for the section's uncategorised lead group. Legal only with a valid `SectionId`.    |
| `SortOrder`                                       | Placement within section and category; ties on declaration order.                                                       |
| `SupportsRead`, `SupportsWrite`, `SupportsAction` | What may be done with it.                                                                                               |
| `Minimum`, `Maximum`, `Step`                      | Inclusive integer bounds and step.                                                                                      |
| `Unit`                                            | `CapabilityUnit`, default `None`.                                                                                       |
| `Choices`                                         | Legal `CapabilityChoice(Value, Display)` options for a choice capability.                                               |
| `MaximumLength`                                   | Required for `Text`, ignored otherwise. No default, so no text value is ever unbounded.                                 |
| `AvailableOnAc`, `AvailableOnDc`                  | Default true. Descriptor fields, not a generation: the live power source is reported through state.                     |
| `Persistence`                                     | `CapabilityPersistence`: `Unknown` (treated as device-persistent by every safety rule), `Volatile`, `DevicePersistent`. |

Placement rules the host applies to the whole set:

- Any role may be placed in a section the set declares.
- A semantic role naming an undeclared section rejects the whole set. Outside a declared layout, a
  power limit belongs under Power on every device.
- A generic role naming an unknown section falls back to a WSGM-owned group; it is not dropped.
- An unplaced capability keeps the semantic home WSGM derives from its role.

### `CapabilityRole`

Serialized as strings. The role is the entire basis on which the overlay and native QAM choose a
control and interpret a value. `CapabilityRoleExtensions.IsGeneric(role)` is an explicit list, not a
prefix check, so making a role placeable is a deliberate decision.

| Role                                                                                                | Meaning                                                                                   |
| --------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `PowerSustainedLimit`, `PowerSlowLimit`, `PowerFastLimit`, `PowerPeakLimit`                         | Processor power limits by window.                                                         |
| `ScenarioMode`                                                                                      | Vendor performance or scenario mode.                                                      |
| `FanMode`, `FanDuty`, `FanTargetRpm`, `FanCurve`, `FanMeasuredRpm`                                  | Fan control and readings per channel.                                                     |
| `ChargeLimit`, `ChargeProtectionMode`, `ChargeBypass`                                               | Battery charge policy.                                                                    |
| `LightingPower`, `LightingBrightness`, `LightingZoneColor`, `LightingEffect`, `LightingEffectSpeed` | Lighting.                                                                                 |
| `Telemetry`                                                                                         | A temperature, power draw or similar reading.                                             |
| `ControllerSource`, `MotionSource`, `HapticSink`                                                    | The controller, its motion sensor and its output sink, as capabilities with availability. |
| `VariableRefreshRate`                                                                               | The device's own panel VRR. The transport is the plugin's (IGCL Arc Sync on Intel parts). |
| `OemControl`                                                                                        | A logical vendor control the user may reassign.                                           |
| `GenericToggle`, `GenericRange`, `GenericChoice`, `GenericAction`, `GenericText`, `GenericReadOnly` | Device-specific controls WSGM has no semantics for. These are the placeable roles.        |

### Value shapes, units and labels

`CapabilityValueKind`: `None` (invoked, not set), `Boolean`, `Integer`, `Choice`, `Color` (24-bit
RGB), `Curve` (ordered `CurvePoint`s), `Text` (bounded plain text).

`CapabilityUnit`: `None`, `Watt`, `Percent`, `Celsius`, `Rpm`, `Milliampere`, `Millivolt`,
`Megahertz`, `Millisecond`. A closed set because WSGM formats and localizes them.

`CapabilityDisplay` carries a `DisplayKey` WSGM localizes, or `Custom` with a `CustomLabel` of at
most 48 characters (`MaxCustomLabelLength`). `TryValidate` rejects an undefined key, a label beside
a real key, a missing label with `Custom`, and any label failing `PlainText`.

| `DisplayKey`                                                      | Rendered as                                                   |
| ----------------------------------------------------------------- | ------------------------------------------------------------- |
| `Custom`                                                          | the bounded `CustomLabel`, not localized                      |
| `Tdp`                                                             | "TDP"                                                         |
| `SustainedPowerLimit`, `BoostPowerLimit`                          | "Sustained power limit", "Boost power limit"                  |
| `PerformanceProfile`                                              | "Performance profile"                                         |
| `FanMode`, `FanSpeed`, `FanCurve`, `FanLeft`, `FanRight`          | "Fan mode", "Fan speed", "Fan curve", "Left fan", "Right fan" |
| `ChargeLimit`, `BypassCharging`                                   | "Charge limit", "Bypass charging"                             |
| `Lighting`, `Brightness`, `LightingEffect`, `LightingEffectSpeed` | "Lighting", "Brightness", "Effect", "Effect speed"            |
| `CpuTemperature`, `Battery`                                       | "CPU temperature", "Battery"                                  |
| `Controller`, `Motion`, `Rumble`                                  | "Controller", "Motion", "Rumble"                              |
| `VariableRefreshRate`                                             | "Variable refresh rate"                                       |

### `CapabilityState` and `CapabilityValue`

State is versioned separately from the descriptor because it changes constantly. It carries only
what the plugin observed; WSGM's desired value and UI progress never mix in.

| `CapabilityState` field                   | Meaning                                                             |
| ----------------------------------------- | ------------------------------------------------------------------- |
| `CapabilityId`, `InstanceId`              | Which instance.                                                     |
| `Available`                               | Whether it can currently be used.                                   |
| `Reason`                                  | `CapabilityReason` when unavailable or degraded; null when healthy. |
| `ObservedValue`                           | The hardware value in the descriptor's shape.                       |
| `Quality`                                 | `HardwareStateQuality`.                                             |
| `ObservedAt`                              | UTC time of the observation.                                        |
| `DescriptorGeneration`, `CycleGeneration` | Generations the state was produced against.                         |

`HardwareStateQuality`: `Unknown` (never read), `Observed` (read, unconfirmed), `Verified` (read
back and confirmed to match what was applied), `Stale` (expired or its generation is gone),
`Faulted`. A successful command without readback earns `Observed` at best.

`CapabilityValue` has a `Kind` and exactly one populated field: `BooleanValue`, `IntegerValue`,
`ChoiceValue`, `ColorValue` (packed 24-bit RGB), `CurveValue` (`IReadOnlyList<CurvePoint>`) or
`TextValue`. `CurvePoint(int Input, int Output)` is one table entry, for example temperature in
Celsius to duty in percent. `CapabilityStateDelta(long Sequence, CapabilityState State)` is one
update as it arrives, with a producer-assigned monotonic sequence.

### `CapabilityCommand` and `CapabilityCommandResult`

| `CapabilityCommand` field      | Meaning                                                                              |
| ------------------------------ | ------------------------------------------------------------------------------------ |
| `CommandId`                    | Correlates the result.                                                               |
| `CapabilityId`, `InstanceId`   | Target instance.                                                                     |
| `RequestedValue`               | The value, or null for an action.                                                    |
| `ExpectedDescriptorGeneration` | Must equal the plugin's current descriptor generation, or the command is `Rejected`. |
| `ExpectedCycleGeneration`      | Must equal the current cycle generation.                                             |
| `Deadline`                     | UTC time after which the command is not worth applying.                              |

| `CommandOutcome`    | Meaning                                                                | Host handling                                                                 |
| ------------------- | ---------------------------------------------------------------------- | ----------------------------------------------------------------------------- |
| `Accepted`          | Validated and queued; nothing reached hardware yet.                    | Waits for the eventual state.                                                 |
| `AppliedUnverified` | Written, no readback available.                                        | Success; state quality stays `Observed`.                                      |
| `AppliedVerified`   | Written and confirmed by an independent read; `ReadbackValue` present. | Success; state may be `Verified`.                                             |
| `Rejected`          | Refused before anything was written.                                   | Shown with its reason.                                                        |
| `TimedOut`          | Deadline passed; unknown whether applied.                              | Not success; never retried automatically.                                     |
| `Indeterminate`     | Interrupted mid-operation; unknown whether applied.                    | Reported to the owning service; never retried blindly for a persistent write. |

`CapabilityCommandResult`: `CommandId`, `Outcome`, `Reason`, `ReadbackValue` (only for
`AppliedVerified`; this field, not the absence of an error, is what lets WSGM call a value
verified), `Rollback`, `CompletedAt`. `RollbackResult`: `NotRequired`, `RestoredVerified`,
`RestoredUnverified`, `RestoreFailed` (the resource is faulted and journalled for reconciliation).

### `CapabilityReason`

`CapabilityReason(CapabilityReasonCode Code, string? Detail = null, bool Retryable = false)`. WSGM
renders the code through its localized strings; `Detail` may name a provider, process or firmware
version and is shown only in diagnostics.

| Code                       | Meaning                                                                        |
| -------------------------- | ------------------------------------------------------------------------------ |
| `Unsupported`              | The device does not implement this capability.                                 |
| `PrerequisiteMissing`      | A provider, driver, library or helper is absent.                               |
| `ResourceConflict`         | Another owner holds the resource.                                              |
| `ResourceReleased`         | The plugin released it, for example when controller management was turned off. |
| `UnavailableOnPowerSource` | Not available on the current power source.                                     |
| `TransportFaulted`         | The transport failed; faulted until recovery.                                  |
| `GenerationChanged`        | The generation changed and this state is not refreshed.                        |
| `ObservationExpired`       | Expired under the freshness policy.                                            |
| `HostUnavailable`          | The plugin runtime is unavailable.                                             |
| `FirmwareNotVerified`      | Firmware outside the verified range.                                           |
| `ValueOutOfRange`          | Outside what the hardware currently accepts.                                   |
| `Quiescing`                | Suspending or shutting down; no new work.                                      |

### `PlainText`

The one rule for plugin-supplied text: labels, titles, descriptions and `Text` values. Such text is
never a format string, markup or localization key; it renders in whatever language the plugin wrote
it.

| Member                                                | Rule                                                                                                                             |
| ----------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------- |
| `TryValidate(value, maximumLength, field, out error)` | Non-blank, at most `maximumLength` characters, no character for which `IsUnsafe` is true. Errors name the field.                 |
| `IsIdentifier(value, maximumLength)`                  | Non-empty, within length, only ASCII letters, digits, `.`, `_`, `-`. Uppercase is allowed because WSGM's own ids are PascalCase. |
| `IsUnsafe(char)`                                      | Any control character, LRM/RLM (U+200E, U+200F), the embedding and override set U+202A–U+202E, and the isolates U+2066–U+2069.   |

## Controller input and haptics

### `CanonicalButtons`

A `[Flags] uint` covering the richest supported handheld. A plugin reports only what its hardware
has; a target renders only what it can represent and drops the rest. Nothing is synthesized or
remapped, and gyro is never converted into stick or mouse movement. The model is complete rather
than minimal because the API version is an exact integer match: adding a control later would be a
breaking rebuild for every plugin.

| Bit   | Button                                        |
| ----- | --------------------------------------------- |
| 0–3   | `A`, `B`, `X`, `Y` (south, east, west, north) |
| 4–5   | `LeftShoulder`, `RightShoulder`               |
| 6–7   | `LeftStick`, `RightStick` (clicks)            |
| 8–10  | `View`, `Menu`, `Guide`                       |
| 11–14 | `DPadUp`, `DPadDown`, `DPadLeft`, `DPadRight` |
| 15–18 | `RearPaddle1` … `RearPaddle4`                 |
| 19–20 | `LeftStickTouch`, `RightStickTouch`           |
| 21–22 | `LeftPadTouch`, `RightPadTouch`               |
| 23–24 | `LeftPadClick`, `RightPadClick`               |
| 25    | `QuickAccess`                                 |

### `CanonicalControllerSample`

Full state, not deltas: a dropped delta leaves a control stuck, a dropped full state is corrected by
the next one. The plugin normalizes axes, since it alone knows raw ranges, centres and inversions.

| Field                               | Range                                                                                                                                                          |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Sequence`                          | Monotonic within one cycle generation.                                                                                                                         |
| `CycleGeneration`                   | Must be current or the sample is dropped.                                                                                                                      |
| `Timestamp`                         | UTC.                                                                                                                                                           |
| `Buttons`                           | `CanonicalButtons`.                                                                                                                                            |
| `LeftStickX/Y`, `RightStickX/Y`     | −1 … 1, Y positive up.                                                                                                                                         |
| `LeftTrigger`, `RightTrigger`       | 0 … 1.                                                                                                                                                         |
| `LeftPadX/Y`, `RightPadX/Y`         | −1 … 1 touch contact position. Two independent contacts: the Deck's trackpads map one each; the DualShock 4's single pad maps first finger left, second right. |
| `LeftPadForce`, `RightPadForce`     | 0 … 1 contact pressure.                                                                                                                                        |
| `LeftStickForce`, `RightStickForce` | 0 … 1 capacitive contact strength.                                                                                                                             |
| `Motion`                            | `MotionSample?`.                                                                                                                                               |
| `Quality`                           | `SampleQuality`, default `Good`.                                                                                                                               |

`Neutral(sequence, cycleGeneration, timestamp)` is the all-at-rest sample WSGM sends to the target
whenever forwarding stops (UI capture, target removal, game exit, suspend, disconnect, disable,
fault), so a held control never stays latched.

`SampleQuality`: `Good`; `ReportLoss` (reports were lost since the previous sample, so edge
detection may have missed a press); `Discontinuity` (the stream restarted); `FirstSampleUnreliable`
(the reference controller can deliver a corrupt first state with every axis at its extreme).

`MotionSample`: `GyroX/Y/Z` in degrees per second with `HasGyro`; `AccelX/Y/Z` in g with
`HasAccelerometer`; optional `SensorTimestamp`. The two are independent because hardware and
operating-system sensor stacks may expose one without the other; a plugin never synthesizes the
missing source.

### Haptic output

`HapticOutputFrame` travels from the virtual target back to the plugin with its own
`TargetGeneration`: a target can be replaced while output is in flight, and a frame for a removed
target must not drive whatever took its slot.

| Member                              | Meaning                                                                        |
| ----------------------------------- | ------------------------------------------------------------------------------ |
| `TargetGeneration`                  | Generation of the virtual target that produced the frame.                      |
| `LowFrequency`, `HighFrequency`     | 0 … 1 motor intensity.                                                         |
| `LeftTrigger`, `RightTrigger`       | 0 … 1 trigger haptic intensity where supported.                                |
| `Timestamp`                         | UTC.                                                                           |
| `Stop(targetGeneration, timestamp)` | A frame with every channel at zero. Rumble always needs an explicit stop path. |
| `IsSilent`                          | True when every channel is ≤ 0.                                                |

`HapticCapabilities` declares per channel (`LowFrequency`, `HighFrequency`, `LeftTrigger`,
`RightTrigger`) whether the device drives it (`OutputChannelSupport.Native`) or discards it
(`Unsupported`, the default), plus:

| Member                  | Default         | Meaning                                                                                                                                                                                                                  |
| ----------------------- | --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `MaxFramesPerSecond`    | 60              | Highest frame rate the device accepts.                                                                                                                                                                                   |
| `MinimumStartIntensity` | 0               | Lowest intensity the motors reliably render. Zero for a voice coil or LRA; an ERM motor does not start below roughly a third of full drive. The host maps bounded haptic events (not continuous rumble) onto this floor. |
| `MinimumPulse`          | `TimeSpan.Zero` | Shortest perceptible pulse. Zero for millisecond actuators; ERM motors need tens of milliseconds to spin up. The host stretches bounded events to at least this length and leaves continuous output untouched.           |
| `Clamp(frame)`          | –               | Returns the frame with unsupported channels zeroed. Channels are dropped, never redistributed.                                                                                                                           |

Device Lab's `test hardware --action haptic-sweep` measures the two motor values interactively. The
reference Claw's ERM motors measured `0.22` and `10 ms`.

### OEM controls

A separate channel from the gamepad. Face buttons, sticks, triggers and the D-pad are not
expressible here, so a plugin can publish vendor controls without turning the canonical channel into
a remapper. The host owns every action vocabulary and decides which mapping is compatible with a
placement.

| `OemControlDescriptor` field    | Meaning                                                                                                                                                                                                                                                          |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ControlId`                     | Stable id within the device definition, for example `oem1`.                                                                                                                                                                                                      |
| `Display`                       | Label (`CapabilityDisplay`).                                                                                                                                                                                                                                     |
| `Placement`                     | `OemControlPlacement.Front` or `Rear`.                                                                                                                                                                                                                           |
| `SupportsLongPress`             | Whether the source distinguishes a long press.                                                                                                                                                                                                                   |
| `RequiresControllerAcquisition` | Whether the control disappears when controller management is off. Declared, not inferred: on the reference handheld the rear paddles are visible only in the acquisition mode the plugin selects, while the front buttons arrive over a separate vendor channel. |

`OemControlEvent(ControlId, OemPressKind Press, long SourceGeneration, DateTimeOffset Timestamp, string DeduplicationId, OemControlEdge Edge = Pressed)`.
`OemPressKind` is `Short` or `Long`. JSON writes those names and still accepts the legacy numeric
values 0 and 1; `OemControlEdge` is `Pressed` or `Released`. The deduplication id must be equal
across every source reporting the same physical press: a vendor event channel and a raw-input path
can both see it, and without a shared id one press would toggle the QAM open and closed.

### `PhysicalDeviceIdentity`

One HID interface the plugin owns: `InstancePath` (used verbatim as the HidHide entry),
`LocationPath`, `VendorId`, `ProductId` (four uppercase hex digits) and `RequiresHiding` (whether
hiding this interface is required for controller management).

## Settings

A setting is a preference WSGM stores and hands back. A capability writes hardware and the device
keeps the value. A control that writes to the device when the user moves it is a capability, however
much it reads like a preference.

### `PluginSettingDescriptor`

| Field                        | Rule                                                                                                                                                                                    |
| ---------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SettingId`                  | Identifier, at most 64 characters.                                                                                                                                                      |
| `ValueKind`                  | `Boolean`, `Integer`, `Choice`, `Color` or `Text`. `None` is refused (use a capability for an action); `Curve` is refused (declare a profile instead) so a curve cannot have two homes. |
| `Display`                    | Must validate.                                                                                                                                                                          |
| `Default`                    | Same kind as `ValueKind` and must pass `TryValidateValue`.                                                                                                                              |
| `SectionId`                  | Optional. An unknown or absent section places the setting in a WSGM-owned fallback and is logged, never dropped.                                                                        |
| `SortOrder`                  | Placement within the section.                                                                                                                                                           |
| `Minimum`, `Maximum`, `Step` | All required for `Integer`, with `Minimum ≤ Maximum` and `Step > 0`.                                                                                                                    |
| `Unit`                       | A defined `CapabilityUnit`.                                                                                                                                                             |
| `Choices`                    | For `Choice`: 1 … 64 entries, each `Value` an identifier ≤ 64, unique, with valid display. Empty for other kinds.                                                                       |
| `MaximumLength`              | For `Text`: 1 … 256. Null for other kinds.                                                                                                                                              |

`TryValidate(out error)` answers whether the declaration is coherent.
`TryValidateValue(value, out error)` answers whether a stored value still fits the current
declaration: kind match, required field present, integer within range and on step (measured from
`Minimum`), choice among declared values, colour within `0x000000 … 0xFFFFFF`, text passing
`PlainText` within `MaximumLength`. A stored value that no longer validates is replaced by
`Default`.

### `PluginSettingsManifest` and `PluginSettingSection`

`Sections` (at most `MaxSections = 12`) and `Settings` (at most `MaxSettings = 96`), each unique by
id and validating on its own. A null collection or item is invalid, including after deserialization.
The limits exist because an unbounded page cannot be navigated with a gamepad.

`PluginSettingSection`: `SectionId` (identifier ≤ 64), `Key` (`SettingSectionKey`), `CustomTitle` (≤
48, required with `Custom`, forbidden otherwise), `SortOrder`.

`SettingSectionKey`: `Custom`, `General`, `Power`, `Fans`, `Lighting`, `Controller`, `Display`,
`Advanced`, `Diagnostics`. The same vocabulary titles overlay sections and categories.

`DeviceSettingValue(string SettingId, CapabilityValue Value)` is one validated effective value,
delivered to `ApplySettingsAsync` as part of the complete set.

## Package manifest: `plugin.wsgm.json`

Exactly six camelCase fields. An unknown member rejects the document. Hardware identity,
dependencies, capabilities, glyphs and recovery policy are published by plugin code or fixed package
data, never by the manifest.

```json
{
  "id": "wsgm.device.msi.claw-8-a2vm",
  "name": "MSI Claw 8 AI+ A2VM",
  "version": "1.2.0",
  "apiVersion": 3,
  "entryAssembly": "WSGM.Device.Msi.Claw8A2Vm.dll",
  "entryType": "WSGM.Device.Msi.Claw8A2Vm.Claw8A2VmPlugin"
}
```

`PluginManifestReader.Read(ReadOnlySpan<byte>)` never throws for bad input. It rejects on size
before any allocation proportional to the input, deserializes with `MaxDepth = 16`, then runs the
field rules. The result is `PluginManifestReadResult(Manifest, Errors)`; `IsValid` is true exactly
when the manifest is non-null and there are no errors. Each `ManifestValidationError` carries the
field `Path`, a stable `ManifestValidationCode` and a message.

| Field           | Rule                                                                                                                                            | Code on failure                                      |
| --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| document        | ≤ 262,144 bytes                                                                                                                                 | `DocumentTooLarge`                                   |
| document        | non-empty, well-formed, no unknown members, depth ≤ 16, not null                                                                                | `MalformedDocument`                                  |
| `id`            | required; ≤ 128; ASCII letters, digits, `.`, `-`, `_`                                                                                           | `MissingField`, `LimitExceeded`, `InvalidIdentifier` |
| `name`          | required; ≤ 256                                                                                                                                 | `MissingField`, `LimitExceeded`                      |
| `version`       | required; canonical dotted numeric with 2–4 components that round-trips through `System.Version` (`1.0`, `1.0.0`, `1.0.0.0`; not `1` or `01.0`) | `MissingField`, `InvalidVersion`                     |
| `apiVersion`    | equals `DeviceApi.Version`                                                                                                                      | `InvalidApiVersion`                                  |
| `entryAssembly` | required; ≤ 260; relative; no `:`; no empty, `.` or `..` segment; `.dll` extension                                                              | `MissingField`, `UnsafePath`                         |
| `entryType`     | required; ≤ 256; ASCII letters, digits, `.`, `_`, `+`, `` ` ``                                                                                  | `MissingField`, `InvalidIdentifier`                  |

`ManifestLimits`: `MaxDocumentBytes = 256 KiB`, `MaxDepth = 16`, `MaxIdLength = 128`,
`MaxDisplayTextLength = 256`, `MaxPathLength = 260`.

## Glyph packages

Glyph data is static package content: artwork for the physical controller and a map from canonical
controls to that artwork. WSGM validates it and owns every Avalonia and Steam adaptation. Asset
handling checks integrity (hash, bounds, well-formedness) and passes the author's bytes through
unchanged; it is an ownership boundary, not a sandbox.

### Layout (`GlyphPackageLayout`)

| Path                                   | Content                                                           |
| -------------------------------------- | ----------------------------------------------------------------- |
| `glyphs/profiles/<profileId>.json`     | One `GlyphProfileManifest`; the file name must equal `profileId`. |
| `glyphs/assets/<sha256>.svg` or `.png` | One asset, addressed only by its lowercase SHA-256.               |
| notice path named by the manifest      | The licence or attribution notice (`.md` or `.txt`).              |

`ProfileManifest(profileId)` and `Asset(sha256, format)` build these paths and throw on an
identifier or hash of the wrong shape.

### `GlyphProfileManifest`

Schema version 1, camelCase JSON, unknown members rejected, depth ≤ 12.

| Field              | Rule                                                                                                                                                     |
| ------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `schemaVersion`    | Must be 1.                                                                                                                                               |
| `profileId`        | Identifier ≤ 128; must equal the file name.                                                                                                              |
| `displayName`      | Plain text ≤ 128, no control characters.                                                                                                                 |
| `revision`         | Positive integer.                                                                                                                                        |
| `exactDeviceIds`   | ≤ 32 unique identifiers naming the device definitions the profile applies to.                                                                            |
| `sourceRevision`   | Identifier ≤ 128, kept for attribution and reproducibility.                                                                                              |
| `noticePath`       | Relative, forward slashes, ≤ 256, no leading `/`, no `\` or `:`, no `.` or `..` segment, only identifier characters per segment, ending `.md` or `.txt`. |
| `assets`           | ≤ 128 `GlyphAssetLockEntry`, unique by hash, aggregate `byteCount` ≤ 4 MiB.                                                                              |
| `controllerImages` | Optional `fullSha256`, `leftSha256`, `rightSha256`, each resolving to an asset of the matching role.                                                     |
| `controls`         | ≤ 64 `GlyphControlMapping`, unique by control.                                                                                                           |
| `aliases`          | ≤ 64 `GlyphControlAlias`, unique by logical control.                                                                                                     |

`GlyphAssetLockEntry`: `sha256` (64 lowercase hex), `format` (`Svg` or `Png`), `byteCount` (1 …
524,288), `role` (`Control`, `FullController`, `LeftController`, `RightController`), and exactly one
of `viewBox` for SVG (positive width and height, every extent within ±4096) or
`pixelWidth`/`pixelHeight` for PNG (each ≤ 4096, product ≤ 4,194,304).

`GlyphControlMapping`: `control` (`GlyphControlId`), `presence` (`Present` or `Absent`), `side`
(`None`, `Left`, `Right`), `physicalLabel` (plain text ≤ 32), `assetSha256` (must resolve to a
`Control` asset; forbidden when `Absent`; null means the generic fallback).

`GlyphControlAlias(logicalControl, physicalControl)` presents one logical control with another's
artwork. The target must be a distinct, present, mapped control and must not itself be aliased.

`GlyphControlId`: `FaceSouth`, `FaceEast`, `FaceWest`, `FaceNorth`, `DpadUp`, `DpadDown`,
`DpadLeft`, `DpadRight`, `LeftStick`, `RightStick`, `LeftStickTouch`, `RightStickTouch`,
`LeftShoulder`, `RightShoulder`, `LeftTrigger`, `RightTrigger`, `Guide`, `View`, `Menu`,
`QuickAccess`, `RearM1`, `RearM2`, `RearLeft2`, `RearRight2`, `Oem1`, `Oem2`, `Touchscreen`,
`LeftTrackpad`, `RightTrackpad`.

### Import (`GlyphPackageImporter.Import(IGlyphPackageSource)`)

1. Enumerate profile ids. A failure is one `ProfileEnumerationFailed` error and an empty result.
2. For the first 32 ids in ordinal order: refuse a non-identifier (`ProfileManifestInvalid`) or a
   duplicate (`DuplicateProfile`), then load the profile. More than 32 discovered ids adds a
   `ProfileManifestInvalid` error rather than truncating silently; the directory source enumerates
   one past the limit for exactly that reason.
3. Load the profile: read the manifest under 256 KiB (`ProfileManifestMissing`), deserialize
   (`ProfileManifestInvalid`), validate every field rule above, check the file-name identity
   (`ProfileIdentityMismatch`). Any error stops the profile.
4. Order the manifest deterministically: device ids, assets by hash, controls by id, aliases by
   logical then physical control.
5. For each asset: read under 512 KiB (`AssetMissing`), compare byte count and SHA-256
   (`AssetRejected`), then normalize SVG or inspect PNG.
6. Validate the notice: present, non-empty, ≤ 256 KiB, strict UTF-8, only `\r`, `\n`, `\t` as
   control characters (`NoticeRejected`).
7. A profile joins `Profiles` only with no error; otherwise all of its errors join `Errors`. Both
   lists are sorted deterministically. `IsValid` is true when there are no errors.

SVG rules: strict UTF-8, bounded well-formed XML with an `svg` root, a view box (or intrinsic size)
matching the lock entry. The author's bytes are kept intact for Steam. Separately, the paths WSGM's
own Avalonia renderer can draw are extracted into `NormalizedGlyphSvg.Paths` (each a
`NormalizedGlyphPath` with data, fill, stroke, stroke width, fill rule, cap and join resolved
through enclosing groups) under `MaxSvgPaths = 256`, `MaxSvgCommands = 4096` and
`MaxPathDataLength = 64 KiB`. Drawing features the renderer does not understand affect only that
local projection; the document still imports and still reaches Steam.

PNG rules: the eight-byte signature and IHDR must be present and the header dimensions must match
the declared pixel width and height. The exact bytes are retained as `ImportedGlyphAsset.RasterPng`.

`ImportedGlyphProfile` is the validated, ordered manifest plus `Assets` keyed by hash.
`ImportedGlyphAsset.RetainedBytes` is the payload size a bounded cache accounts for.

### Sources

`IGlyphPackageSource` supplies files from one already selected package: `EnumerateProfileIds()` and
`TryRead(relativePath, maximumBytes, out bytes)`. Implementations own root confinement,
reparse-point rejection and bounded reads.

`ImmutableGlyphPackageDirectorySource(packageRoot)` is the shipped implementation. It refuses an
absent or reparse-point root at construction and a profiles path that is not a plain directory. It
enumerates only plain `*.json` files whose names are identifiers (sorted, distinct, 33 at most),
constrains every relative path under the root, verifies that every existing path component is plain
before opening, after opening and after reading, and opens with `FileShare.Read` so the bytes cannot
be replaced underneath it. Every I/O failure reads as "not readable" rather than throwing.

## Serialization

`DeviceJsonContext` is the source-generated `JsonSerializerContext` for `PluginManifest` and
`GlyphProfileManifest`: camelCase property names, unknown members disallowed, compact output. Enums
marked with `JsonStringEnumConverter<T>` in this SDK (`DeviceCycleState`, the handoff enums, every
capability enum, `SampleQuality`, `OutputChannelSupport`, the OEM enums, `SettingSectionKey` and the
glyph enums) serialize as their names.

## Test kit

`TestPluginHostAdapter(long cycleGeneration)` is the in-memory `IPluginHostAdapter` for plugin
tests. It records every publication in order:

| Property                                | Content                                                                           |
| --------------------------------------- | --------------------------------------------------------------------------------- |
| `DescriptorSets`                        | Every descriptor replacement.                                                     |
| `CapabilityStates`                      | Every state publication.                                                          |
| `PhysicalDeviceSets`, `PublishedOutput` | Every physical-device list and the most recent haptic capabilities.               |
| `ControllerSamples`                     | Every sample.                                                                     |
| `OemControlSets`, `OemEvents`           | Every control set and event.                                                      |
| `SettingsManifests`                     | Every declared manifest.                                                          |
| `Traces`                                | Every `(Level, Scope, Message)`, so a test can assert that a decision was traced. |

Every publication throws `ArgumentNullException` for a null item and honours a cancelled token. The
adapter validates nothing else; assert the SDK `TryValidate` rules yourself where they matter.
Combine it with `PluginTrace.Install(adapter)` to capture the plugin's own diagnostics.

## Rules a plugin must follow

The compiler catches none of these; the host relies on all of them.

- Detect without side effects. `DetectAsync` opens nothing mutable and matches exactly; an unknown
  board, firmware or range returns `Matched = false` with a reason.
- Revalidate on every command: identity, firmware, range and current state. Then check
  `ExpectedDescriptorGeneration` and `ExpectedCycleGeneration` and return `Rejected` with
  `GenerationChanged` when either is stale.
- Report the truth. `AppliedVerified` only with a `ReadbackValue`; `AppliedUnverified` without
  readback; `TimedOut` or `Indeterminate` when the outcome is unknown. Never retry an uncertain
  persistent write yourself.
- Publish whole sets. Descriptors, OEM controls and physical devices replace what came before. Bump
  the descriptor generation whenever any descriptor changes.
- Stamp generations. Every sample, state and descriptor set carries the current cycle generation; a
  stale one is dropped.
- Restore what you changed. Capture original state before writing volatile settings, restore it on
  stop or failure, and record in the state directory only what could not be restored.
- Keep the controller handoff ordered: stop reading, restore the original mode, verify
  re-enumeration by location path, then report the furthest step reached.
- Declare dependencies, never install them. A missing prerequisite makes one capability unavailable
  with `PrerequisiteMissing`.
- Trace decisions, not samples. Install `PluginTrace` first thing in `StartAsync`; one `Failure`
  line at the top of every catch; `Change` for anything a poll loop observes; `Debug` for detail
  that only matters mid-investigation; nothing at all in the 125 Hz loop.
- Own no UI. Labels, titles, icons and units come from the closed vocabularies; custom text is
  bounded plain text.

## Limits at a glance

| Limit                                                        | Value                   | Defined on                                                                       |
| ------------------------------------------------------------ | ----------------------- | -------------------------------------------------------------------------------- |
| API version                                                  | 3                       | `DeviceApi.Version`                                                              |
| Trace message                                                | 1024 chars              | `PluginTrace.MaxMessageLength`                                                   |
| Custom label                                                 | 48                      | `CapabilityDisplay.MaxCustomLabelLength`                                         |
| Overlay sections per set                                     | 16                      | `CapabilitySection.MaxSections`                                                  |
| Categories per section                                       | 16                      | `CapabilitySection.MaxCategories`                                                |
| Section / category id                                        | 64                      | `CapabilitySection.MaxSectionIdLength`, `CapabilityCategory.MaxCategoryIdLength` |
| Section / category custom title                              | 48                      | `MaxCustomTitleLength`                                                           |
| Section custom description                                   | 96                      | `CapabilitySection.MaxCustomDescriptionLength`                                   |
| Settings sections                                            | 12                      | `PluginSettingsManifest.MaxSections`                                             |
| Settings                                                     | 96                      | `PluginSettingsManifest.MaxSettings`                                             |
| Setting id                                                   | 64                      | `PluginSettingDescriptor.MaxSettingIdLength`                                     |
| Choice options per setting                                   | 64                      | `PluginSettingDescriptor.MaxChoices`                                             |
| Text setting length ceiling                                  | 256                     | `PluginSettingDescriptor.MaxTextLength`                                          |
| Settings section id / title                                  | 64 / 48                 | `PluginSettingSection`                                                           |
| Manifest document                                            | 256 KiB, depth 16       | `ManifestLimits`                                                                 |
| Manifest id / text / path                                    | 128 / 256 / 260         | `ManifestLimits`                                                                 |
| Haptic frame rate default                                    | 60 fps                  | `HapticCapabilities.MaxFramesPerSecond`                                          |
| Glyph profile document                                       | 256 KiB, depth 12       | `GlyphProfileLimits.MaxDocumentBytes`                                            |
| Glyph asset                                                  | 512 KiB                 | `GlyphProfileLimits.MaxAssetBytes`                                               |
| Glyph profile aggregate                                      | 4 MiB                   | `GlyphProfileLimits.MaxProfileBytes`                                             |
| Glyph notice                                                 | 256 KiB                 | `GlyphProfileLimits.MaxNoticeBytes`                                              |
| Glyph dimension / raster pixels                              | 4096 / 4,194,304        | `GlyphProfileLimits`                                                             |
| Glyph assets / profiles / controls / aliases / exact devices | 128 / 32 / 64 / 64 / 32 | `GlyphProfileLimits`                                                             |
| Glyph identifier / display name / physical label             | 128 / 128 / 32          | `GlyphProfileLimits`                                                             |
| SVG paths / commands / path data                             | 256 / 4096 / 64 KiB     | `GlyphProfileLimits`                                                             |
| Notice path                                                  | 256                     | `GlyphPackageImporter`                                                           |

## Device power presets

`CapabilityDescriptor.PowerPresets` defaults to an empty list. Assignment copies the supplied
collection into a read-only snapshot; later array or list edits cannot alter a published descriptor.
A plugin may declare up to 16 `DevicePowerPreset` records on its single-instance sustained watt
limit. Each supplies a stable `Id`, a plain-text `Name`, `SustainedWatts`, `SlowWatts`, and
`WindowsMode` (`BetterBattery`, `Balanced`, or `BestPerformance`). Windows modes are separate from
power plans.

Optional `ScenarioOnAc` and `ScenarioOnDc` targets select a firmware scenario before the watt
limits. Declare both or neither. They must name choices of exactly one single-instance, readable,
writable `ScenarioMode` capability available on both power sources. A host must know the current
power source, confirm the scenario readback, re-read the watt pair after the scenario command, and
include the scenario in preset matching. A source change during application stops remaining writes
without retry. Scenario targets are one-shot selections, not stored desired-state policy. This
extends the existing preset and scenario vocabulary without exposing device registers; firmware
scenario choices can describe MSI SHIFT modes or another device's thermal modes.

`DevicePowerPreset.TryValidate` checks the complete descriptor set: exactly one readable, writable
sustained/slow watt pair, targets inside both ranges and steps, sustained <= slow, unique IDs of
1-64 ASCII letters/digits/dots/underscores/hyphens, and names bounded to 120 characters. `custom` is
reserved for the host's observed state. Other roles cannot carry presets.

The host applies these as explicit shortcuts through existing capability commands and its Windows
backend. It derives Custom when any observed target differs; it never reapplies a preset because
values changed. A multi-control failure can leave a partial result, which must be reported without
an automatic retry. No plugin gets Windows handles or UI responsibilities through this contract.
`SdkPowerPresetTests` covers serialization, defaults, target validation, and declaration bounds.

The optional preset metadata was added within API 3 with an empty default; no existing descriptor
changes meaning.

During consolidation, `OemPressKind` gained the same JSON string converter as the other OEM enums.
It writes `Short`/`Long` and preserves numeric reads and the existing enum ordinals within API 3.

## Version history

`CapabilityDescriptor.PairedPowerLimitId` and `CapabilityCommand.ApplyPowerPair` are optional API 3
additions, defaulting to null and false. A sustained watt descriptor may name one single-instance
readable/writable `PowerSlowLimit` descriptor. Its range and step may differ from the primary. The
primary range defines valid coordinated targets; the plugin maps those targets to companion values
within the companion range and step. Validate the complete set with `DevicePowerPair.TryValidate`. A
paired command asks the plugin to apply its coordinated target, verify both limits and roll back
both after failure. Verified result readback contains the sustained value. Ordinary commands retain
independent-limit behavior. Hosts must not assume equal watt limits. Restoration applies the
sustained pair followed by the separately captured original boost limit. Existing equal-limit
plugins remain valid without changes. This contract covers a two-limit envelope; additional platform
and Windows-policy dimensions remain separate work.

| API | Change                                                                                                                                                                                                                                                                                                             |
| --- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 1   | Initial contract: lifecycle, capabilities, canonical input and haptics, OEM controls, settings manifest, glyph packages, manifest validation, test kit.                                                                                                                                                            |
| 2   | Overlay section vocabulary: `CapabilityDescriptorSet.Sections`, `CapabilitySection`, `CapabilityCategory`, `SectionIcon`, and `CategoryId`/`SortOrder` on `CapabilityDescriptor`. `HapticCapabilities.MinimumStartIntensity` and `MinimumPulse` were added within version 2 as additive fields with zero defaults. |
| 3   | Suppressed and repeat-aware diagnostics: `DeviceTraceLevel.Debug`, `PluginTrace.Debug`, `PluginTrace.Change`, and `IPluginHostAdapter.TraceChange`. The adapter member has a default implementation so version 2 hosts and test doubles continue to compile.                                                       |
