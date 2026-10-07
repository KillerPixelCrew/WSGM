# WSGM.Device.Sdk reference

The public contract in `WSGM.Device.Sdk`, type by type, with every rule and limit the host applies
to what a plugin publishes. The XML documentation on each member is the authoritative wording; this
document lets the contract be read as a whole, in the order a plugin experiences it. How WSGM hosts
a plugin (discovery, package files, the load context, deadlines, the overlay, profiles and the
controller path) is not covered here.

Related:

- WSGM `docs/device-plugin-system.md`: the host mechanism.
- WSGM `docs/device-plugin-authoring.md`: building, testing, packing and installing a package.
- [Device Lab](https://github.com/KillerPixelCrew/WSGM/tree/master/src/WSGM.DeviceLab): the
  authoring tool.

| Fact               | Value                                                                              |
| ------------------ | ---------------------------------------------------------------------------------- |
| Assembly / package | `WSGM.Device.Sdk`                                                                  |
| Target framework   | `net10.0-windows`, matching the host that loads the plugin                         |
| Dependencies       | none; a plugin inherits nothing from the SDK                                       |
| API version        | `DeviceApi.Version = 12`; WSGM, Device Lab and every plugin require an exact match |
| Package version    | `0.5.0`; pre-1.0, a breaking change moves the minor version                        |
| Licence            | MIT (WSGM itself is GPL-3.0-or-later)                                              |
| Documentation      | every public member is documented; an undocumented member fails the build          |

## The contract at a glance

A plugin is one class implementing `IDevicePlugin`, shipped with a `plugin.wsgm.json` that also
declares the hardware it is for and the capability roles it publishes. WSGM loads it in-process and
drives it through one lifecycle per WSGM run. Everything crossing the boundary is a semantic record:
no transport, handle, path, script or UI travels in either direction.

```text
 WSGM ──────────────────────────────────────────────────────────────► plugin
   DetectAsync(PluginDetectionContext)                exact identity match, no side effects
   StartAsync(PluginStartContext)                     one cycle begins; host adapter handed over
   ApplySettingsAsync(values)                         every declared setting, as a full set
   ExecuteCommandAsync(CapabilityCommand)             one semantic write or action
   ApplyHapticOutputAsync(HapticOutputFrame)          virtual-target output → physical motors
   SetControllerManagementAsync(...)                  controller ownership on/off inside a cycle
   SuspendAsync / ResumeAsync                         quiesce for sleep/lock, revalidate after
   ReleaseControllerAsync(...)                        let go of the physical pad, best effort
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

Two integers travel with descriptor sets, capability states and commands so that a stale one can be
refused rather than applied late:

- Cycle generation (`long`), advanced by the host at every start and resume. Turning controller
  management on or off does not advance it: the cycle continues. A descriptor set, state or command
  carrying an old cycle generation is refused.
- Descriptor generation (`long`), owned by the plugin and incremented whenever any descriptor
  changes. A command authored against an older descriptor generation is refused, because the range
  it was validated against no longer exists.

Controller samples, OEM events and haptic frames carry no generation. A sample and a haptic frame
are each the whole current state, so a newer one simply replaces an older one, and an OEM event is
deduplicated by its `DeduplicationId`.

## Lifecycle: `IDevicePlugin`

The entry type named by the manifest. The host constructs it with its public parameterless
constructor; it lives for one WSGM run and is `IAsyncDisposable`. The host awaits `StopAsync` before
it calls `DisposeAsync`, and disposal only releases handles: it never writes the hardware. Lifecycle
calls are serialized, so a plugin never sees two at once, but commands and haptic frames can arrive
while a background service the plugin started is running.

The cancellation token passed to a lifecycle call is the host's deadline for that call. A plugin
that ignores it keeps the host waiting until the outer application deadline, after which WSGM
proceeds with its own cleanup and records the plugin's answer as unverified.

| Member                                                                | When the host calls it                                                                                                                                | What the plugin does                                                                                                                                                                                                                    |
| --------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `PackageId`                                                           | Any time.                                                                                                                                             | Return the stable id from `plugin.wsgm.json`.                                                                                                                                                                                           |
| `DetectAsync(PluginDetectionContext, ct)`                             | Once per run, before any mutable work; also by Device Lab's `test plugin`.                                                                            | Compare `context.Identity` with the device definitions it knows. Return `Matched` with a `DeviceDefinitionId`, or `Matched = false` with a `Reason`. Acquire nothing mutable.                                                           |
| `StartAsync(PluginStartContext, ct)`                                  | Once, after a match, when Device Integration is enabled.                                                                                              | Install `PluginTrace`, open transports, publish the settings manifest, descriptor set, physical devices, OEM controls, then initial state. On cancellation unwind whatever was acquired. Return the aggregate `PluginOperationalState`. |
| `ApplySettingsAsync(values, ct)`                                      | Once after start and on every change, always with every declared setting. Never called when no settings were declared.                                | Take the validated preferences into account. This is not a hardware-write path. The default implementation is a no-op.                                                                                                                  |
| `ExecuteCommandAsync(CapabilityCommand, ct)`                          | On user intent from the overlay, Settings, the native QAM, profile application, or a Device Lab attended action.                                      | Revalidate identity, firmware, range and current state, check both generations, apply, read back where possible, return a truthful `CapabilityCommandResult`. A readback only upgrades the result; it never gates the write.            |
| `SuspendAsync(PluginQuiesceContext, ct)`                              | On sleep or session lock.                                                                                                                             | Start no long operation, stop sampling and output, hold or close handles; finish before `Deadline`.                                                                                                                                     |
| `ResumeAsync(PluginResumeContext, ct)`                                | After wake or unlock.                                                                                                                                 | Revalidate identity, reacquire under the new `CycleGeneration`, republish descriptors and state, return the aggregate state.                                                                                                            |
| `GetDiagnosticsAsync(ct)`                                             | For the diagnostics snapshot in the overlay and the log.                                                                                              | Return bounded key/value facts about services and recovery. No transports, secrets or identifiers.                                                                                                                                      |
| `ApplyHapticOutputAsync(HapticOutputFrame, ct)`                       | Whenever the virtual target emits output.                                                                                                             | Drive the motors. Each frame is the whole motor state and replaces the previous one. Never trace per frame.                                                                                                                             |
| `ReleaseControllerAsync(PluginControllerReleaseContext, ct)`          | When controller management is turned off (`ControllerOnly`) and when the cycle ends (`FullDeactivation`), after WSGM has silenced its virtual target. | Stop the motors and the reader, close handles and write the original controller mode back. Best effort: trace what failed and return. Nothing is reported, and the host waits for no readback.                                          |
| `SetControllerManagementAsync(PluginControllerManagementContext, ct)` | When the user toggles controller management while the cycle continues.                                                                                | Acquire or release the physical controller only, in the same cycle generation; republish the controller and haptic capability states.                                                                                                   |
| `StopAsync(PluginStopContext, ct)`                                    | At the end of the cycle, for one of the `PluginStopReason` values.                                                                                    | Restore every temporarily changed hardware state, release everything, report `Clean`, `Unverified` or `Failed` truthfully.                                                                                                              |
| `DisposeAsync()`                                                      | After stop, before the collectible load context unloads.                                                                                              | Release every remaining handle, even when an earlier owner throws. Never write the hardware; the journal covers a cycle that was not stopped.                                                                                           |

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
| `PluginQuiesceContext`              | `Deadline Deadline`                                                                                                                         |                                                                                                           |
| `PluginResumeContext`               | `long CycleGeneration`, `Deadline Deadline`                                                                                                 | New generation for everything reopened.                                                                   |
| `PluginControllerReleaseContext`    | `HandoffScope Scope`, `Deadline Deadline`                                                                                                   |                                                                                                           |
| `PluginControllerManagementContext` | `bool Enabled`, `Deadline Deadline`                                                                                                         | The cycle and its generation continue: turning the controller on is not a new device.                     |
| `PluginStopContext`                 | `PluginStopReason Reason`, `Deadline Deadline`                                                                                              |                                                                                                           |
| `PluginStopReason`                  | `WsgmExiting`, `IntegrationDisabled`, `Updating`, `SessionEnding`, `Uninstalling`, `StartCanceled`, `StartFailed`, `RuntimeFault`           | `Updating` and `Uninstalling` arrive with the compressed cleanup budget.                                  |

## Publishing: `IPluginHostAdapter`

The publication surface in `PluginStartContext.Host`, valid for the whole cycle. WSGM validates
every publication; an invalid one is refused (logged, previous value kept) rather than partially
applied.

| Member                                                                 | Contract                                                                                                                                                                                                                                                                         |
| ---------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `long CycleGeneration`                                                 | The generation in force. Stamp it on every capability state and descriptor set.                                                                                                                                                                                                  |
| `PublishDescriptorsAsync(CapabilityDescriptorSet, ct)`                 | Replaces the whole set. Carries a new `Generation` when anything changed, and the current `CycleGeneration`. Validation rules are under Capabilities.                                                                                                                            |
| `PublishCapabilityStateAsync(CapabilityState, ct)`                     | One observation for one capability instance, stamped with the descriptor generation it was produced against.                                                                                                                                                                     |
| `PublishPhysicalDevicesAsync(devices, HapticCapabilities? output, ct)` | The HID interfaces the plugin owns, whether each must be hidden for controller management, and what the motors can do. `output = null` declares no haptic sink.                                                                                                                  |
| `PublishControllerSampleAsync(CanonicalControllerSample, ct)`          | A full pad state. WSGM keeps only the newest sample it has not yet routed. Never trace here.                                                                                                                                                                                     |
| `PublishOemControlsAsync(controls, ct)`                                | The closed set of vendor controls. WSGM renders them as assignable rows.                                                                                                                                                                                                         |
| `PublishOemEventAsync(OemControlEvent, ct)`                            | One press or release edge, deduplicated by `DeduplicationId`.                                                                                                                                                                                                                    |
| `PublishSettingsManifestAsync(PluginSettingsManifest, ct)`             | A declaration WSGM draws, validates, stores and localizes. A manifest that fails `TryValidate` is refused and the previous one kept.                                                                                                                                             |
| `Trace(DeviceTraceLevel, scope, message)`                              | Synchronous, void, never throws. Best-effort and unordered with respect to publications. Recorded whole.                                                                                                                                                                         |
| `TraceChange(DeviceTraceLevel, scope, key, message)`                   | Same contract, plus a key the host uses to suppress an unchanged repeat and count it.                                                                                                                                                                                            |
| `ReportFault(scope, message)`                                          | Default implementation traces at `Error`. WSGM's adapter also closes command admission, releases the controller, stops and disposes the plugin, and restarts it under the bounded fault policy. Use only for failures of plugin-started work that outlive their initiating call. |

### `PluginTrace`

A static, ambient sink shaped like WSGM's own `Log`: a no-op until `Install(adapter)` is called,
normally as the first statement of `StartAsync`. `DeviceTraceLevel` is `Info`, `Warn`, `Error`,
`Debug` — declared in that order so the values that existed before `Debug` did not move.

| Member                                      | Behaviour                                                                                                                                              |
| ------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
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

## Cycle state and controller release

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

### Controller release

WSGM lets go of the controller in a fixed order: it stops forwarding and leaves its virtual target
neutral, calls `ReleaseControllerAsync`, removes the virtual target, and only then removes its own
HidHide entries so the physical pad reappears (a fault restart keeps it hidden, since it takes the
pad again at once). Un-hiding first would expose a device the plugin still holds, and Steam and the
running game would see both controllers at once. Each step runs whatever the one before did, and a
failure is only logged: nothing waits for a readback and nothing is retried. The release returns
nothing, so a plugin traces what did not work rather than reporting it.

`HandoffScope`: `ControllerOnly` (the cycle and every non-controller resource continue, including
the OEM event path) or `FullDeactivation` (WSGM is exiting or Device Integration was turned off).

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
require a declaration. WSGM adds every shared page a plugin did not declare when it validates and
projects a layout, and renders only populated pages. WSGM's Windows energy controls keep Power
populated even when device integration is disabled.

A section is a page of the Device overlay; a category is a heading on that page. Both travel inside
the set so layout and content replace atomically. For custom sections, the plugin chooses placement,
order, a title key and an icon; WSGM owns every string, geometry and control shape.
`TryValidate(out error)` on both types applies exactly these rules.

| `CapabilitySection` field | Rule                                                                                 |
| ------------------------- | ------------------------------------------------------------------------------------ |
| `SectionId`               | Identifier (`PlainText.IsIdentifier`).                                               |
| `Key`                     | A `SettingSectionKey` WSGM localizes, or `Custom`.                                   |
| `CustomTitle`             | Required plain text when `Key` is `Custom`; must be null otherwise.                  |
| `CustomDescription`       | Optional plain text for the section card; null means WSGM's own wording for the key. |
| `Icon`                    | A `SectionIcon`; the default `None` lets WSGM derive one from the key.               |
| `SortOrder`               | Placement among sections; ties break on declaration order.                           |
| `Categories`              | Unique ids, each validating on its own.                                              |

`CapabilityCategory`: `CategoryId` (identifier), `Key`, `CustomTitle` (same rule as above),
`SortOrder`. Identifiers and text are checked for shape, not length, and a set may declare any
number of sections and categories.

`SectionIcon`: `None`, `Power`, `Fan`, `Battery`, `Lighting`, `Controller`, `Display`, `Gauge`,
`Wrench`.

### `CapabilityDescriptor`

Immutable. When firmware, endpoints or dependency health change what a capability can do, the plugin
publishes a complete replacement set under a new generation. A descriptor is a description, not a
promise: WSGM validates against it for UI consistency; the plugin revalidates on every command.

| Field                                             | Meaning                                                                                                                                                                                                                                                                                                                     |
| ------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CapabilityId`                                    | Stable id such as `power.primary-limit`.                                                                                                                                                                                                                                                                                    |
| `InstanceId`                                      | Discriminator when a device has several of one capability (two fans).                                                                                                                                                                                                                                                       |
| `Role`                                            | What it means to WSGM (`CapabilityRole`).                                                                                                                                                                                                                                                                                   |
| `ValueKind`                                       | Shape of its value (`CapabilityValueKind`).                                                                                                                                                                                                                                                                                 |
| `Display`                                         | Its label (`CapabilityDisplay`).                                                                                                                                                                                                                                                                                            |
| `SectionId`                                       | A section declared in the same set, or for a `Generic*` role a settings-manifest section.                                                                                                                                                                                                                                   |
| `CategoryId`                                      | A category of that section, or null for the section's uncategorised lead group. Legal only with a valid `SectionId`.                                                                                                                                                                                                        |
| `SortOrder`                                       | Placement within section and category; ties on declaration order.                                                                                                                                                                                                                                                           |
| `Prominence`                                      | Closed host-owned `CapabilityProminence`: Normal (default), Primary or Compact.                                                                                                                                                                                                                                             |
| `LayoutPair`                                      | Optional exact companion capability and instance in the same section/category; presentation only.                                                                                                                                                                                                                           |
| `SupportsRead`, `SupportsWrite`, `SupportsAction` | What may be done with it.                                                                                                                                                                                                                                                                                                   |
| `Minimum`, `Maximum`, `Step`                      | Inclusive integer bounds and step.                                                                                                                                                                                                                                                                                          |
| `Unit`                                            | `CapabilityUnit`, default `None`.                                                                                                                                                                                                                                                                                           |
| `Choices`                                         | Legal `CapabilityChoice(Value, Display)` options for a choice capability.                                                                                                                                                                                                                                                   |
| `MaximumLength`                                   | Required for `Text`, ignored otherwise. No default, so no text value is ever unbounded.                                                                                                                                                                                                                                     |
| `AvailableOnAc`, `AvailableOnDc`                  | Default true. Descriptor fields, not a generation: the live power source is reported through state.                                                                                                                                                                                                                         |
| `Persistence`                                     | `CapabilityPersistence`: `Unknown` (treated as device-persistent by every safety rule), `Volatile`, `DevicePersistent`.                                                                                                                                                                                                     |
| `ProfileScope`                                    | `CapabilityProfileScope`: `Switched` (default; WSGM writes the running game's value on every game change), `GlobalOnly` (one machine-wide value, never per game), `NativePerApplication` (WSGM writes the Global value and hands each game's overrides to a `wsgm.gpu` publisher, whose driver applies them at game start). |
| `ApplyTiming`                                     | `CapabilityApplyTiming`: `Immediate` (default), `NextApplicationStart`, `SystemRestart`. WSGM shows it on the row; it never delays or repeats a write because of it.                                                                                                                                                        |

Placement rules the host applies to the whole set:

- Any role may be placed in a section the set declares.
- A semantic role naming an undeclared section rejects the whole set. Outside a declared layout, a
  power limit belongs under Power on every device.
- A generic role naming an unknown section falls back to a WSGM-owned group; it is not dropped.
- An unplaced capability keeps the semantic home WSGM derives from its role.

The optional layout hints remain semantic. `Prominence` is the closed `CapabilityProminence`
vocabulary: `Normal` (the default), `Primary` (full-width principal control), or `Compact`
(secondary control or reading). Hosts choose final dimensions and preserve accessible editors.
`LayoutPair` optionally names one `CapabilityId` and `InstanceId` in the same explicitly assigned
section and category. A pair requires a nonempty `SectionId`; role-based fallback placement cannot
establish a shared group. The host keeps the companion adjacent when possible. It is independent of
`PairedPowerLimitId` and never authorizes another hardware write. `CapabilityLayout.TryValidate`
rejects unknown prominence, missing or ambiguous companions, self-pairs and cross-group pairs. WSGM
validates the complete set; Device Lab validates these hints before an attended capability action.
Change descriptor generation whenever either hint changes. Plugins still cannot publish markup,
colours, dimensions or templates.

Overlay observations update existing rows by capability identity within their descriptor and device
cycle. A new descriptor generation replaces the layout; ordinary state publications update values,
availability and status in place without committing readback as user intent.

### `CapabilityRole`

Serialized as strings. The role is the entire basis on which the overlay and native QAM choose a
control and interpret a value. `CapabilityRoleExtensions.IsGeneric(role)` is an explicit list, not a
prefix check, so making a role placeable is a deliberate decision.

| Role                                                                                                | Meaning                                                                                                            |
| --------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------ |
| `PowerSustainedLimit`, `PowerSlowLimit`, `PowerFastLimit`, `PowerPeakLimit`                         | Processor power limits by window.                                                                                  |
| `ScenarioMode`                                                                                      | Vendor performance or scenario mode.                                                                               |
| `FanMode`, `FanDuty`, `FanTargetRpm`, `FanCurve`, `FanMeasuredRpm`                                  | Fan control and readings per channel.                                                                              |
| `ChargeLimit`, `ChargeProtectionMode`, `ChargeBypass`                                               | Battery charge policy.                                                                                             |
| `LightingPower`, `LightingBrightness`, `LightingZoneColor`, `LightingEffect`, `LightingEffectSpeed` | Lighting.                                                                                                          |
| `Telemetry`                                                                                         | A temperature, power draw or similar reading.                                                                      |
| `ControllerSource`, `MotionSource`, `HapticSink`                                                    | The controller, its motion sensor and its output sink, as capabilities with availability.                          |
| `VariableRefreshRate`                                                                               | Panel VRR. The transport is the publisher's; on Intel graphics it is the `wsgm.gpu.intel` package's IGCL Arc Sync. |
| `OemControl`                                                                                        | A logical vendor control the user may reassign.                                                                    |
| `GenericToggle`, `GenericRange`, `GenericChoice`, `GenericAction`, `GenericText`, `GenericReadOnly` | Device-specific controls WSGM has no semantics for. These are the placeable roles.                                 |

### Value shapes, units and labels

`CapabilityValueKind`: `None` (invoked, not set), `Boolean`, `Integer`, `Choice`, `Color` (24-bit
RGB), `Curve` (ordered `CurvePoint`s), `Text` (plain text within the descriptor's `MaximumLength`).

`CapabilityUnit`: `None`, `Watt`, `Percent`, `Celsius`, `Rpm`, `Milliampere`, `Millivolt`,
`Megahertz`, `Millisecond`. A closed set because WSGM formats and localizes them.

`CapabilityDisplay` carries a `DisplayKey` WSGM localizes, or `Custom` with a plain-text
`CustomLabel`. `TryValidate` rejects an undefined key, a label beside a real key, a missing label
with `Custom`, and any label failing `PlainText`.

| `DisplayKey`                                                      | Rendered as                                                   |
| ----------------------------------------------------------------- | ------------------------------------------------------------- |
| `Custom`                                                          | the plugin's `CustomLabel`, not localized                     |
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
`TextValue`. The static factories `CapabilityValue.None()`, `Boolean`, `Integer`, `Choice`, `Color`,
`Curve` and `Text` build a value of that kind with its one field set; `Curve` stores the list it is
given. `CurvePoint(int Input, int Output)` is one table entry, for example temperature in Celsius to
duty in percent.

### `CapabilityCommand` and `CapabilityCommandResult`

| `CapabilityCommand` field      | Meaning                                                                              |
| ------------------------------ | ------------------------------------------------------------------------------------ |
| `CommandId`                    | Correlates the result.                                                               |
| `CapabilityId`, `InstanceId`   | Target instance.                                                                     |
| `RequestedValue`               | The value, or null for an action.                                                    |
| `ExpectedDescriptorGeneration` | Must equal the plugin's current descriptor generation, or the command is `Rejected`. |
| `ExpectedCycleGeneration`      | Must equal the current cycle generation.                                             |
| `Deadline`                     | Active-time moment after which the command is not worth applying.                    |

| `CommandOutcome`    | Meaning                                                                | Host handling                                                                              |
| ------------------- | ---------------------------------------------------------------------- | ------------------------------------------------------------------------------------------ |
| `Accepted`          | Validated and kept for a later apply; nothing reached hardware yet.    | WSGM returns it for a native per-application value it saved to the running game's profile. |
| `AppliedUnverified` | Written, no readback available.                                        | Success; state quality stays `Observed`.                                                   |
| `AppliedVerified`   | Written and confirmed by an independent read; `ReadbackValue` present. | Success; state may be `Verified`.                                                          |
| `Rejected`          | Refused before anything was written.                                   | Shown with its reason.                                                                     |
| `TimedOut`          | Deadline passed; unknown whether applied.                              | Not success; never retried automatically.                                                  |
| `Indeterminate`     | Interrupted mid-operation; unknown whether applied.                    | Reported to the owning service; never retried blindly for a persistent write.              |

`CapabilityCommandResult`: `CommandId`, `Outcome`, `Reason`, `ReadbackValue` (an independent
readback for `AppliedVerified` only; an `AppliedUnverified` write is published as observed
capability state), `Rollback`, `CompletedAt`. `RollbackResult`: `NotRequired`, `RestoredVerified`,
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
it. The rule sets no length of its own: a `Text` value is held to the `MaximumLength` its descriptor
or setting declares, and labels and identifiers are as long as the plugin wrote them.

| Member                                                | Rule                                                                                                                                    |
| ----------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------- |
| `TryValidate(value, field, out error)`                | Non-blank, no character for which `IsUnsafe` is true. Errors name the field.                                                            |
| `TryValidate(value, maximumLength, field, out error)` | The same rule, plus at most the declared `maximumLength` characters.                                                                    |
| `IsIdentifier(value)`                                 | Non-empty, only ASCII letters, digits, `.`, `_`, `-`; no length is imposed. Uppercase is allowed because WSGM's own ids are PascalCase. |
| `IsUnsafe(char)`                                      | Any control character, LRM/RLM (U+200E, U+200F), the embedding and override set U+202A–U+202E, and the isolates U+2066–U+2069.          |

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
`CanonicalControllerSample` and its optional `MotionSample` are readonly record structs so creating,
copying, and publishing each high-rate frame does not allocate contract objects.

| Field                               | Range                                                                                                                                                          |
| ----------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `Timestamp`                         | UTC.                                                                                                                                                           |
| `Buttons`                           | `CanonicalButtons`.                                                                                                                                            |
| `LeftStickX/Y`, `RightStickX/Y`     | −1 … 1, Y positive up.                                                                                                                                         |
| `LeftTrigger`, `RightTrigger`       | 0 … 1.                                                                                                                                                         |
| `LeftPadX/Y`, `RightPadX/Y`         | −1 … 1 touch contact position. Two independent contacts: the Deck's trackpads map one each; the DualShock 4's single pad maps first finger left, second right. |
| `LeftPadForce`, `RightPadForce`     | 0 … 1 contact pressure.                                                                                                                                        |
| `LeftStickForce`, `RightStickForce` | 0 … 1 capacitive contact strength.                                                                                                                             |
| `Motion`                            | `MotionSample?`.                                                                                                                                               |

A sample carries no sequence, generation or quality flag. A plugin that cannot trust a report does
not publish it: the reference controller's first report can arrive with every axis at its extreme,
and the Claw plugin skips it.

`Neutral(timestamp)` is the all-at-rest sample WSGM sends to the target whenever forwarding stops
(UI capture, target removal, game exit, suspend, disconnect, disable, fault), so a held control
never stays latched.

`MotionSample`: `GyroX/Y/Z` in degrees per second with `HasGyro`; `AccelX/Y/Z` in g with
`HasAccelerometer`; optional `SensorTimestamp`. The two are independent because hardware and
operating-system sensor stacks may expose one without the other; a plugin never synthesizes the
missing source. Motion rides on the controller sample, and a plugin reads its sensors for as long as
it owns the controller; there is no host signal to start or stop them.

### Haptic output

`HapticOutputFrame` travels from the virtual target back to the plugin. Like an input sample it is
the whole motor state, so a newer frame replaces an older one and nothing needs to say which target
or generation it came from. It is a readonly record struct, so the rumble path from the virtual
target to the plugin allocates nothing per frame.

| Member                          | Meaning                                                                        |
| ------------------------------- | ------------------------------------------------------------------------------ |
| `LowFrequency`, `HighFrequency` | 0 … 1 motor intensity.                                                         |
| `LeftTrigger`, `RightTrigger`   | 0 … 1 trigger haptic intensity where supported.                                |
| `Timestamp`                     | UTC.                                                                           |
| `Stop(timestamp)`               | A frame with every channel at zero. Rumble always needs an explicit stop path. |
| `IsSilent`                      | True when every channel is ≤ 0.                                                |

`HapticCapabilities` declares per channel (`LowFrequency`, `HighFrequency`, `LeftTrigger`,
`RightTrigger`) whether the device drives it (`OutputChannelSupport.Native`) or discards it
(`Unsupported`, the default), plus:

| Member                  | Default         | Meaning                                                                                                                                                                                                                  |
| ----------------------- | --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `MaxFramesPerSecond`    | 60              | Highest frame rate the device accepts.                                                                                                                                                                                   |
| `MinimumStartIntensity` | 0               | Lowest intensity the motors reliably render. Zero for a voice coil or LRA; an ERM motor does not start below roughly a third of full drive. The host maps bounded haptic events (not continuous rumble) onto this floor. |
| `MinimumPulse`          | `TimeSpan.Zero` | Shortest perceptible pulse. Zero for millisecond actuators; ERM motors need tens of milliseconds to spin up. The host stretches bounded events to at least this length and leaves continuous output untouched.           |
| `Clamp(frame)`          | –               | Returns the frame with unsupported channels zeroed, without allocating. Channels are dropped, never redistributed.                                                                                                       |

Device Lab's `test hardware --action haptic-sweep` measures the two motor values interactively. The
reference Claw's ERM motors measured `0.22` and `10 ms`.

### OEM controls

A separate channel from the gamepad. Face buttons, sticks, triggers and the D-pad are not
expressible here, so a plugin can publish vendor controls without turning the canonical channel into
a remapper. The host owns every action vocabulary and decides which mapping is compatible with a
placement.

| `OemControlDescriptor` field    | Meaning                                                                                                                                                                                                                                                                                         |
| ------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `ControlId`                     | Stable id within the device definition, for example `oem1`.                                                                                                                                                                                                                                     |
| `Display`                       | Label (`CapabilityDisplay`).                                                                                                                                                                                                                                                                    |
| `Placement`                     | `OemControlPlacement.Front` or `Rear`.                                                                                                                                                                                                                                                          |
| `SupportsLongPress`             | Whether the source distinguishes a long press.                                                                                                                                                                                                                                                  |
| `RequiresControllerAcquisition` | Whether the control disappears when controller management is off. Declared, not inferred: on the reference handheld the rear paddles are visible only in the acquisition mode the plugin selects, while the front buttons arrive over a separate vendor channel.                                |
| `CompanionApplication`          | Whether this is the button the manufacturer prints for its own companion application (Armoury Crate, Legion Space). Physical metadata: the host decides what an unassigned one does; WSGM opens its overlay. Leave it false when the plugin already routes the button to Guide or Quick Access. |

`OemControlEvent(ControlId, OemPressKind Press, DateTimeOffset Timestamp, string DeduplicationId, OemControlEdge Edge = Pressed)`.
`OemPressKind` is `Short` or `Long`, written to JSON by name; `OemControlEdge` is `Pressed` or
`Released`. The deduplication id must be equal across every source reporting the same physical
press: a vendor event channel and a raw-input path can both see it, and without a shared id one
press would toggle the QAM open and closed.

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
| `SettingId`                  | Identifier.                                                                                                                                                                             |
| `ValueKind`                  | `Boolean`, `Integer`, `Choice`, `Color` or `Text`. `None` is refused (use a capability for an action); `Curve` is refused (declare a profile instead) so a curve cannot have two homes. |
| `Display`                    | Must validate.                                                                                                                                                                          |
| `Default`                    | Same kind as `ValueKind` and must pass `TryValidateValue`.                                                                                                                              |
| `SectionId`                  | Optional. An unknown or absent section places the setting in a WSGM-owned fallback and is logged, never dropped.                                                                        |
| `SortOrder`                  | Placement within the section.                                                                                                                                                           |
| `Minimum`, `Maximum`, `Step` | All required for `Integer`, with `Minimum ≤ Maximum` and `Step > 0`.                                                                                                                    |
| `Unit`                       | A defined `CapabilityUnit`.                                                                                                                                                             |
| `Choices`                    | For `Choice`: at least one entry, each `Value` a unique identifier with valid display. Empty for other kinds.                                                                           |
| `MaximumLength`              | For `Text`: positive, chosen by the plugin. Null for other kinds.                                                                                                                       |

`TryValidate(out error)` answers whether the declaration is coherent.
`TryValidateValue(value, out error)` answers whether a stored value still fits the current
declaration: kind match, required field present, integer within range and on step (measured from
`Minimum`), choice among declared values, colour within `0x000000 … 0xFFFFFF`, text passing
`PlainText` within `MaximumLength`. A stored value that no longer validates is replaced by
`Default`.

### `PluginSettingsManifest` and `PluginSettingSection`

`Sections` and `Settings`, each unique by id and validating on its own. A null collection or item is
invalid, including after deserialization. Neither collection has a count limit.

`PluginSettingSection`: `SectionId` (identifier), `Key` (`SettingSectionKey`), `CustomTitle`
(required with `Custom`, forbidden otherwise), `SortOrder`.

`SettingSectionKey`: `Custom`, `General`, `Power`, `Fans`, `Lighting`, `Controller`, `Display`,
`Advanced`, `Diagnostics`. The same vocabulary titles overlay sections and categories.

`DeviceSettingValue(string SettingId, CapabilityValue Value)` is one validated effective value,
delivered to `ApplySettingsAsync` as part of the complete set.

## Deadlines and the active clock

Every lifecycle context and every `CapabilityCommand` carries a `Deadline`, not a wall-clock time.
It is measured on `ActiveClock`, which advances only while the process can run: a dedicated thread
observes it at least every 250 ms, and a longer step between two observations, which can only mean
the process was frozen, counts as one second. A suspend that is mid-flight when a handheld enters
Modern Standby therefore resumes after the wake with the budget it had, instead of failing on an
expired wall-clock deadline.

| Member                                   | Use                                                                        |
| ---------------------------------------- | -------------------------------------------------------------------------- |
| `Deadline.After(TimeSpan)`               | A deadline that much active time from now.                                 |
| `Remaining`, `HasExpired`                | What is left; never negative.                                              |
| `CreateCancellationSource(params token)` | A source cancelled by the active clock, not a wall timer, plus any tokens. |
| `Earliest(other)`                        | The earlier of two deadlines.                                              |
| `Deadline.Never`, `Deadline.Expired`     | The two ends.                                                              |
| `Deadline.At(DateTimeOffset)`            | For a host whose own budget is wall time, such as an installer handshake.  |

Use `CreateCancellationSource` rather than
`CancellationTokenSource.CancelAfter(deadline.Remaining)`: a wall timer fires the moment a frozen
process thaws.

## Package manifest: `plugin.wsgm.json`

CamelCase fields; an unknown member rejects the document. Besides identity and the entry point, the
manifest declares as data what a host must know before it loads any code: the hardware the package
is for and every capability role it may publish. WSGM setup matches `hardware` against the machine
to decide whether to install the package, and derives the system components it installs (for example
the virtual-controller drivers for `ControllerSource`) from `capabilities`. The host refuses a
descriptor set that uses an undeclared role. `DetectAsync` still confirms the exact machine once the
plugin runs; dependencies, glyphs and recovery policy stay in plugin code or fixed package data.

```json
{
  "id": "wsgm.device.msi.claw",
  "name": "MSI Claw",
  "version": "1.4.0",
  "apiVersion": 12,
  "entryAssembly": "WSGM.Device.Msi.Claw.dll",
  "entryType": "WSGM.Device.Msi.Claw.ClawPlugin",
  "hardware": [
    {
      "baseboardManufacturer": "Micro-Star International Co., Ltd.",
      "baseboardProduct": "MS-1T41"
    },
    {
      "baseboardManufacturer": "Micro-Star International Co., Ltd.",
      "baseboardProduct": "MS-1T52"
    }
  ],
  "capabilities": ["PowerSustainedLimit", "FanCurve", "ControllerSource"]
}
```

The sample is shortened from the Claw package's manifest, which lists all five Claw baseboards; a
rule may also narrow a board by `systemSku` when a package needs it.

`wsgmVersion` is absent from a source manifest. Packing writes the WSGM release the package is built
for, and WSGM refuses a package whose `wsgmVersion` is not its own.

`PluginManifestReader.Read(ReadOnlySpan<byte>)` never throws for bad input. It rejects on size
before any allocation proportional to the input, deserializes with `MaxDepth = 16`, then runs the
field rules. The result is `PluginManifestReadResult(Manifest, Errors)`; `IsValid` is true exactly
when the manifest is non-null and there are no errors. Each `ManifestValidationError` carries the
field `Path`, a stable `ManifestValidationCode` and a message.

| Field           | Rule                                                                                                                                            | Code on failure                          |
| --------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------- |
| document        | ≤ 262,144 bytes                                                                                                                                 | `DocumentTooLarge`                       |
| document        | non-empty, well-formed, no unknown members, depth ≤ 16, not null                                                                                | `MalformedDocument`                      |
| `id`            | required; lowercase ASCII letters, digits, `.`, `-`, `_`, starting with a letter or digit                                                       | `MissingField`, `InvalidIdentifier`      |
| `name`          | required; plain text without control or bidirectional formatting characters                                                                     | `MissingField`, `InvalidText`            |
| `version`       | required; canonical dotted numeric with 2–4 components that round-trips through `System.Version` (`1.0`, `1.0.0`, `1.0.0.0`; not `1` or `01.0`) | `MissingField`, `InvalidVersion`         |
| `apiVersion`    | equals `DeviceApi.Version`                                                                                                                      | `InvalidApiVersion`                      |
| `entryAssembly` | required; a `.dll` file name at the package root: ASCII letters, digits, `.`, `_`, `-`, not starting with `.`                                   | `MissingField`, `UnsafePath`             |
| `entryType`     | required; ASCII letters, digits, `.`, `_`, `+` (no generic type)                                                                                | `MissingField`, `InvalidIdentifier`      |
| `hardware`      | optional; `HardwareMatchRule`s; each sets at least one field; each field non-blank plain text                                                   | `InvalidText`, `MissingField`            |
| `capabilities`  | optional; distinct `CapabilityRole` names; an unknown name is a malformed document                                                              | `InvalidIdentifier`, `MalformedDocument` |
| `wsgmVersion`   | optional; canonical dotted numeric version                                                                                                      | `InvalidVersion`                         |

### Package layout (`PluginPackageLayout`)

The archive rules WSGM applies when it opens a `.wsgmpkg`, and Device Lab and the packers apply too.
`ReadEntries(Stream)` reads every file into memory and throws `InvalidDataException` for an archive
over `MaxPackageBytes` (512 MiB on disk or uncompressed), a file over `MaxFileBytes` (128 MiB), an
unsafe entry name (`TryNormalizeEntryName`: relative, `/`-separated, no `\`, `:`, empty, `.` or `..`
segment), two names that differ only in case, or a native image (`IsImageName` and `IsManagedImage`:
every `.dll`, `.exe` and `.sys` must carry a CLR header). Nothing limits how many files a package
holds. `HostProvidedAssemblies` names the assemblies WSGM always supplies itself, which a packer
leaves out.

### `HardwareMatchRule` and `HardwareMatcher`

A rule's fields are `baseboardManufacturer`, `baseboardProduct`, `systemModel`, `systemSku`,
`processorName`, `processorNameContains`, `baseboardVersion` and `fallback`. Every field that is set
must match the `DeviceIdentitySnapshot`, compared case-insensitively after trimming;
`processorNameContains` is a substring test and `systemModel` is compared with `SystemProduct`. A
rule with no field set never matches. `HardwareMatcher.Match(rules, identity)` returns the first
exact rule that matches, else the first matching fallback, with one explanation per compared field.
Device Lab's knowledge base and WSGM setup use the same rule and matcher.

`ManifestLimits`: `MaxDocumentBytes = 256 KiB` and `MaxDepth = 16`, the parser bounds. The
document's size already bounds every field in it, so fields are checked for shape, not for length or
count.

## Glyph packages

Glyph data is static package content: artwork for the physical controller and a map from canonical
controls to that artwork. WSGM validates it and owns every Avalonia and Steam adaptation. Asset
handling checks integrity (identifiers, confined paths, bounds, well-formedness) and passes the
author's bytes through unchanged; it is an ownership boundary, not a sandbox.

### Layout (`GlyphPackageLayout`)

| Path                                    | Content                                                           |
| --------------------------------------- | ----------------------------------------------------------------- |
| `glyphs/profiles/<profileId>.json`      | One `GlyphProfileManifest`; the file name must equal `profileId`. |
| `glyphs/assets/<assetId>.svg` or `.png` | One asset, addressed only by its `assetId`.                       |
| notice path named by the manifest       | The licence or attribution notice (`.md` or `.txt`).              |

`ProfileManifest(profileId)` and `Asset(assetId, format)` build these paths and throw on an
identifier of the wrong shape.

### `GlyphProfileManifest`

Schema version 1, camelCase JSON, unknown members rejected, depth ≤ 12.

| Field              | Rule                                                                                                                                              |
| ------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------- |
| `schemaVersion`    | Must be 1.                                                                                                                                        |
| `profileId`        | Identifier; must equal the file name.                                                                                                             |
| `displayName`      | Plain text, no control characters.                                                                                                                |
| `revision`         | Positive integer.                                                                                                                                 |
| `exactDeviceIds`   | Unique identifiers naming the device definitions the profile applies to.                                                                          |
| `sourceRevision`   | Identifier, kept for attribution and reproducibility.                                                                                             |
| `noticePath`       | Relative, forward slashes, no leading `/`, no `\` or `:`, no `.` or `..` segment, only identifier characters per segment, ending `.md` or `.txt`. |
| `assets`           | `GlyphAssetEntry` records, unique by `assetId`; the supplied bytes total ≤ 4 MiB.                                                                 |
| `controllerImages` | Optional `fullAssetId`, `leftAssetId`, `rightAssetId`, each resolving to an asset of the matching role.                                           |
| `controls`         | `GlyphControlMapping` records, unique by control.                                                                                                 |
| `aliases`          | `GlyphControlAlias` records, unique by logical control.                                                                                           |

`GlyphAssetEntry`: `assetId` (identifier, naming the file under `glyphs/assets`), `format` (`Svg` or
`Png`), `role` (`Control`, `FullController`, `LeftController`, `RightController`,
`ControlHighlight`), and exactly one of `viewBox` for SVG (positive width and height, every extent
within ±4096) or `pixelWidth`/`pixelHeight` for PNG (each ≤ 4096, product ≤ 4,194,304).

`GlyphControlMapping`: `control` (`GlyphControlId`), `presence` (`Present` or `Absent`), `side`
(`None`, `Left`, `Right`), `physicalLabel` (plain text), `assetId` (must resolve to a `Control`
asset; forbidden when `Absent`; null means the generic fallback), `highlightAssetId` (must resolve
to a `ControlHighlight` asset; forbidden when `Absent`; null means selecting the control lights
nothing on the controller diagram), `softPullAssetId` (must resolve to a `Control` asset; forbidden
when `Absent`; null draws `assetId` for a partial pull too; meaningful for triggers), `steamGlyph`
(the file name, without extension, of a glyph Steam ships under `/steaminputglyphs/`, such as
`xbox_button_logo`; Steam draws it for the control in place of `assetId`; forbidden when `Absent`).

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
4. Order the manifest deterministically: device ids, assets by `assetId`, controls by id, aliases by
   logical then physical control.
5. For each asset: read under 512 KiB (`AssetMissing`), charge the bytes against the 4 MiB profile
   budget (`AssetRejected`), then normalize SVG or inspect PNG.
6. Validate the notice: present, non-empty, ≤ 256 KiB, strict UTF-8, only `\r`, `\n`, `\t` as
   control characters (`NoticeRejected`).
7. A profile joins `Profiles` only with no error; otherwise all of its errors join `Errors`. Both
   lists are sorted deterministically. `IsValid` is true when there are no errors.

SVG rules: strict UTF-8, bounded well-formed XML with an `svg` root, a view box (or intrinsic size)
matching the lock entry. The author's bytes are kept intact for Steam. Separately, the paths WSGM's
own Avalonia renderer can draw are extracted into `NormalizedGlyphSvg.Paths` (each a
`NormalizedGlyphPath` with data, fill, stroke, stroke width, fill rule, cap and join resolved
through enclosing groups), every non-blank path however many there are. Drawing features the
renderer does not understand affect only that local projection; the document still imports and still
reaches Steam.

PNG rules: the eight-byte signature and IHDR must be present and the header dimensions must match
the declared pixel width and height. Animation chunks are refused; text and other ancillary chunks
are skipped. The exact bytes are retained as `ImportedGlyphAsset.RasterPng`.

`ImportedGlyphProfile` is the validated, ordered manifest plus `Assets` keyed by package-scoped
asset identifier. `ImportedGlyphAsset.RetainedBytes` is the payload size a bounded cache accounts
for.

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

## Shared helpers

Optional building blocks that more than one handheld needs. They are not part of the host boundary:
nothing in the contract requires them, and a plugin may use its own.

| Type                                         | Namespace | What it does                                                                                                                                                                                                                                                                                                                                                                                                       |
| -------------------------------------------- | --------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `DeviceReconnect`                            | `Windows` | Polls every half second for a pad or HID collection that dropped off the bus (every sleep) and reopens it once; a failed reopen is not retried.                                                                                                                                                                                                                                                                    |
| `HidDevices`, `HidCollection`, `DeviceNode`  | `Windows` | Finds, opens and writes a handheld's HID collections, and lists the present device nodes a package hands to HidHide. `EnumerateAll` lists every vendor's collections and reports the ones it cannot describe; `Inspect` reads the manufacturer and product strings and the report descriptor's button and value capabilities (`HidCollectionDetails`, `HidCapability`). Both serve diagnostics such as Device Lab. |
| `LegacyMotionSensors`, `MotionSensorReading` | `Windows` | Reads the standard or Intel "Physical" gyrometer and accelerometer through the Sensor API, without WinRT's per-report allocations.                                                                                                                                                                                                                                                                                 |
| `LegacyMotionStream`                         | `Windows` | Streams an open sensor set by driver event, with a precision-timed poll where events cannot be registered.                                                                                                                                                                                                                                                                                                         |
| `LowLevelKeyboardHook`                       | `Windows` | A `WH_KEYBOARD_LL` hook on its own thread, for OEM buttons that arrive as keyboard keys.                                                                                                                                                                                                                                                                                                                           |
| `PrecisionTicker`                            | `Windows` | Wakes a polling thread at a fixed interval with about a millisecond of accuracy, without raising the machine's timer resolution.                                                                                                                                                                                                                                                                                   |
| `MotionSampleBuilder`                        | `Input`   | Turns sensor readings into `MotionSample`s: saturation clip, the device's axis map, then the measured zero-rate offset subtracted.                                                                                                                                                                                                                                                                                 |
| `StationaryGyroBiasCalibrator`               | `Input`   | Measures the gyroscope's zero-rate offset from rest windows.                                                                                                                                                                                                                                                                                                                                                       |
| `GyroFrameResampler`                         | `Input`   | Averages the gyro rate over each controller frame, so a 100 Hz sensor under a 125 Hz pad integrates without a beat.                                                                                                                                                                                                                                                                                                |
| `OemButtonLatch`                             | `Input`   | Holds a release-less Guide or Quick Access press for 200 ms, so the virtual pad delivers it as a tap.                                                                                                                                                                                                                                                                                                              |

The namespaces are `WSGM.Device.Sdk.Windows` and `WSGM.Device.Sdk.Input`.

### Services and recovery

`WSGM.Device.Sdk.Services` holds the service model both first-party packages run their cycle on. A
package keeps its machine-specific services, identity snapshot and recovery state; the SDK keeps the
state machine, the walks that apply results, the command gate with its observation loop, and the
journal file.

Disposal stops the serializer's observation loop but retains its managed gate, so in-flight and
queued work can finish safely. The journal is not disposable; its write gate lives as long as the
journal. Post-command publication and failed-start rollback use active-time deadline tokens;
rollback passes the supplied cycle deadline through every release and retraction.

Only a missing journal file counts as an absent record. A directory, read failure, malformed
document or undefined recovery status blocks mutations and preserves the existing record. Status
updates reject undefined enum values before I/O. A save refused by a transient file lock leaves that
mutation unapplied and allows a later write after the lock is released; it does not latch a
permanent fault.

| Type                            | What it does                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                |
| ------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `DeviceServiceState`            | `Idle`, `Acquiring`, `Owned`, `Passive`, `Degraded`, `Releasing`, `ReleasedUnverified`, `Faulted`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `DeviceCycleContext<TIdentity>` | Cycle generation, the operation's `Deadline`, and the package's identity snapshot.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `DeviceServiceStatus`           | A service's id, state and reason. `Fault` lasts until the next acquisition; `ReconciliationBlockReason` outlives the cycle, and the service itself decides how it acquires and releases while it is set.                                                                                                                                                                                                                                                                                                                                                                                                                                                                    |
| `DeviceService<TIdentity>`      | Adds `AcquireAsync`, `ReleaseAsync` and `SuspendAsync` (a release unless overridden, run only when `Suspendable`).                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| `DeviceServiceLifecycle`        | Acquires, suspends and releases one service and applies the result: an exception faults only that service, an acquisition outside Owned, Passive, Degraded or Faulted faults it, and a release outside Idle, ReleasedUnverified or Faulted, or past its deadline, is unverified. `AcquireAllAsync` walks services in start order, `SuspendAllAsync` (suspendable ones only) and `ReleaseAllAsync` in reverse, and `RollBackStartAsync` releases every service of a failed start and retracts its physical devices, OEM controls and descriptors. Also builds the start and stop results, the default state reason and the `device`/`plugin`/`unavailable` ownership choice. |
| `DeviceCommandSerializer`       | One gate for a plugin's commands, lifecycle transitions (`RunAsync`) and a 10-second observation loop, so a refresh never interleaves with a write. `ExecuteAsync` refuses a command while the cycle is inactive or quiescing, before and after its turn, and after an applied write refreshes and republishes within the command's deadline (at most 2 s); a `ScenarioMode` write whose resulting limits cannot be published is indeterminate. The plugin supplies the accepting descriptor set, the refresh and the publication; `StopObservation` also cancels a post-command publication in flight.                                                                     |
| `DeviceRecoveryJournal<TState>` | `temporary-state.v1.json` in the plugin state directory: one entry per service with the original captured before its first mutation, the firmware it belongs to and the restore status, replaced atomically. A package derives it with its source-generated `DeviceRecoveryDocument<TState>` type information and entry validation, and decides when an entry is restored. `DiagnosticState` reads `blocked`, `pending` or `healthy`.                                                                                                                                                                                                                                       |
| `DeviceRecoveryStatus`          | `Pending`, `RestoredVerified` (removes the entry), `RestoredUnverified`, `RestoreFailed`. The last two are for a package that can read its state back. `BeginAsync` replaces an entry bound to other firmware with the fresh capture and sets an unverified or failed entry on the same firmware pending again; `PendingOriginalFor` is what a release may write back.                                                                                                                                                                                                                                                                                                      |

`CommandResults` (in `Capabilities`) builds the verified, unverified, rejected and indeterminate
`CapabilityCommandResult`s; an unverified result carries no readback, and the plugin publishes the
written value as the observed state. `DiagnosticText.FromException` (in `Plugin`) turns an exception
into one plain line for a reason or a trace. `DeviceWriteBudget` (in `Lifecycle`) refuses a write
when a deadline leaves less than two seconds, through `IsAvailable` or through `Require`, which
throws `DeviceWriteBudgetException` (not a cancellation): a command refused this way is rejected and
may be retried, and a release leaves its recovery entry pending.
`CapabilityValueValidation.ValueMatches` (in `Capabilities`) is the value check WSGM applies before
dispatch, for a plugin to repeat when it revalidates a command. `CapabilityIds` names the capability
ids the first-party packages declare and WSGM persists in profiles, and `SourceOwnership` the
`device`, `plugin` and `unavailable` choices `DeviceServiceLifecycle.Ownership` projects.

## Serialization

`DeviceJsonContext` is the source-generated `JsonSerializerContext` for `PluginManifest` and
`GlyphProfileManifest`: camelCase property names, unknown members disallowed, compact output. Enums
marked with `JsonStringEnumConverter<T>` in this SDK (`DeviceCycleState`, every capability enum,
`OutputChannelSupport`, the OEM enums, `SettingSectionKey` and the glyph enums) serialize as their
names.

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

- Detect without side effects. `DetectAsync` opens nothing mutable and matches exact supported
  identity. Firmware is a detection gate only when the package requires it; the first-party Claw and
  Ally packages do not reject a supported board simply because optional firmware reads fail.
- Revalidate on every command: identity, firmware, range and current state. Then check
  `ExpectedDescriptorGeneration` and `ExpectedCycleGeneration` and return `Rejected` with
  `GenerationChanged` when either is stale.
- Report the truth. `AppliedVerified` with the readback as `ReadbackValue`; `AppliedUnverified` with
  the written value published as observed state and no readback; `TimedOut` or `Indeterminate` when
  the outcome is unknown. Never retry an uncertain persistent write yourself.
- Never gate a write on readback. Write, publish the written value as observed, and let a matching
  readback upgrade the result to verified; a missing or different readback leaves it unverified.
- Publish whole sets. Descriptors, OEM controls and physical devices replace what came before. Bump
  the descriptor generation whenever any descriptor changes.
- Stamp generations. Every capability state and descriptor set carries the current cycle generation;
  a stale one is refused.
- Journal the first original before a temporary mutation when it can be captured, so a process
  failure cannot lose the restore obligation. Restore according to the package's documented
  release/recovery policy; the SDK does not prescribe an immediate rollback after a partial write.
  Clear completed entries, preserve incomplete ones, and never convert observed state to a user
  edit.
- Release the controller as best effort: stop the motors and the reader, write the original mode
  back, trace what failed and return.
- Treat a pad that drops off the bus as a state, not a fault: report the service degraded and take
  the pad again once when it returns (`DeviceReconnect`). A failed reopen is not retried.
- Declare dependencies, never install them. A missing prerequisite makes one capability unavailable
  with `PrerequisiteMissing`.
- Trace decisions, not samples. Install `PluginTrace` first thing in `StartAsync`; one `Failure`
  line at the top of every catch; `Change` for anything a poll loop observes; `Debug` for detail
  that only matters mid-investigation; nothing at all in the 125 Hz loop.
- Own no UI. Labels, titles, icons and units come from the closed vocabularies; custom text is plain
  text.

## Limits at a glance

| Limit                           | Value             | Defined on                              |
| ------------------------------- | ----------------- | --------------------------------------- |
| API version                     | 12                | `DeviceApi.Version`                     |
| Manifest document               | 256 KiB, depth 16 | `ManifestLimits`                        |
| Package file / whole package    | 128 MiB / 512 MiB | `PluginPackageLayout`                   |
| Haptic frame rate default       | 60 fps            | `HapticCapabilities.MaxFramesPerSecond` |
| Glyph profile document          | 256 KiB, depth 12 | `GlyphProfileLimits.MaxDocumentBytes`   |
| Glyph asset                     | 512 KiB           | `GlyphProfileLimits.MaxAssetBytes`      |
| Glyph profile aggregate         | 4 MiB             | `GlyphProfileLimits.MaxProfileBytes`    |
| Glyph notice                    | 256 KiB           | `GlyphProfileLimits.MaxNoticeBytes`     |
| Glyph dimension / raster pixels | 4096 / 4,194,304  | `GlyphProfileLimits`                    |

## Device power presets

`CapabilityDescriptor.PowerPresets` defaults to an empty list. Assignment copies the supplied
collection into a read-only snapshot; later array or list edits cannot alter a published descriptor.
A plugin may declare any number of `DevicePowerPreset` records on its single-instance sustained watt
limit. Each supplies a stable `Id`, a plain-text `Name`, `SustainedWatts`, `SlowWatts`, and
`WindowsMode` (`BetterBattery`, `Balanced`, or `BestPerformance`). Windows modes are separate from
power plans.

Optional `ScenarioOnAc` and `ScenarioOnDc` targets select a firmware scenario before the watt
limits. Declare both or neither. They must name choices of exactly one single-instance, writable
`ScenarioMode` capability available on both power sources; it need not be readable. A host must know
the current power source and include the scenario in preset matching. It trusts the scenario write
and never waits for a readback. A source change during application stops remaining writes without
retry. Scenario targets are one-shot selections, not stored desired-state policy. This extends the
existing preset and scenario vocabulary without exposing device registers; firmware scenario choices
can describe MSI SHIFT modes or another device's thermal modes.

`DevicePowerPreset.TryValidate` checks the complete descriptor set: exactly one writable (not
necessarily readable) sustained/slow watt pair, targets inside both ranges and steps, sustained <=
slow, unique non-empty IDs of ASCII letters/digits/dots/underscores/hyphens, and single-line
plain-text names. `custom` is reserved for the host's observed state. Other roles cannot carry
presets.

The host applies these as explicit shortcuts through existing capability commands and its Windows
backend. It derives Custom when any observed target differs; it never reapplies a preset because
values changed. A multi-control failure can leave a partial result, which must be reported without
an automatic retry. No plugin gets Windows handles or UI responsibilities through this contract.
`SdkPowerPresetTests` covers serialization, defaults, target validation, and unique ids.

## Version history

`CapabilityDescriptor.PairedPowerLimitId` is an optional API 3 addition. A sustained watt descriptor
may name one single-instance writable `PowerSlowLimit` descriptor; its range and step may differ
from the primary. Validate the complete set with `DevicePowerPair.TryValidate`. Since API 11 the
host decides both limits: every write to either one carries the other in
`CapabilityCommand.PairedPowerLimitWatts`, the sustained limit never above the boost limit, and a
unified target moves both to the same wattage within the boost range. The plugin resolves the pair
with `DevicePowerPair.TryResolve`, which refuses a missing or out-of-range companion and a sustained
value above the boost value, and writes both as given in its own firmware order. Verified result
readback contains the commanded value. `DevicePowerPair.Peer` finds the other limit of a pair.
Restoration applies the sustained pair followed by the separately captured original boost limit.
This contract covers a two-limit envelope; additional platform and Windows-policy dimensions remain
separate work.

| API | Change                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                        |
| --- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1   | Initial contract: lifecycle, capabilities, canonical input and haptics, OEM controls, settings manifest, glyph packages, manifest validation, test kit.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                       |
| 2   | Overlay section vocabulary: `CapabilityDescriptorSet.Sections`, `CapabilitySection`, `CapabilityCategory`, `SectionIcon`, and `CategoryId`/`SortOrder` on `CapabilityDescriptor`. `HapticCapabilities.MinimumStartIntensity` and `MinimumPulse` were added within version 2 as additive fields with zero defaults.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| 3   | Suppressed and repeat-aware diagnostics: `DeviceTraceLevel.Debug`, `PluginTrace.Debug`, `PluginTrace.Change`, and `IPluginHostAdapter.TraceChange`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| 4   | Controller and motion samples use readonly record structs to avoid per-sample contract allocation.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            |
| 5   | Optional `Prominence` and `LayoutPair` descriptor hints use normal, unpaired defaults. New descriptor setters require API 5 so older hosts reject incompatible plugin binaries before loading them.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| 6   | Manifest `hardware`, `capabilities` and `wsgmVersion`; `HardwareMatchRule` and `HardwareMatcher`; `DeviceIdentitySnapshot.BaseboardManufacturer` and `ProcessorName`. Hosts refuse descriptors whose role the manifest does not declare.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                      |
| 7   | `OemControlDescriptor.CompanionApplication` marks the manufacturer's companion-application button so the host can give it a default.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                          |
| 8   | `IDevicePlugin.SetMotionDemandAsync` and `PluginMotionDemandContext`: the host's signal that nothing reads motion. The member has a default implementation.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   |
| 9   | Every lifecycle context and `CapabilityCommand` carries a `Deadline` measured on `ActiveClock` instead of a UTC `DateTimeOffset`. Time the process spends frozen by Modern Standby, sleep or hibernation does not count against it.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           |
| 10  | Simpler controller model. `CanonicalControllerSample` loses `Sequence`, `CycleGeneration` and `Quality` (`SampleQuality` is gone), `OemControlEvent` loses `SourceGeneration`, `HapticOutputFrame` loses `TargetGeneration`, and `PluginControllerManagementContext` loses `CycleGeneration`. `ReleaseControllerAsync` is best effort and returns `ValueTask`, so `PluginControllerRelease`, `ControllerHandoffStep` and `ControllerHandoffResult` are gone. `SetMotionDemandAsync` and `PluginMotionDemandContext` are removed: a plugin streams motion for as long as it owns the controller. New shared helpers in `WSGM.Device.Sdk.Windows` and `WSGM.Device.Sdk.Input`.                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                  |
| 11  | Shared service scaffolding: `WSGM.Device.Sdk.Services` (`DeviceService`, `DeviceServiceLifecycle`, `DeviceCommandSerializer`, `DeviceRecoveryJournal`), `CommandResults` and `DiagnosticText`. A plugin built against them cannot load on an older host, so the exact match refuses it. `DeviceDiagnosticsSnapshot`, which no host assembled, is removed. `HidDevices.EnumerateAll`, `HidDevices.Inspect` (`HidCollectionDetails`, `HidCapability`, `HidReportType`) and `HidCollection.ReleaseNumber` are new. `HapticOutputFrame` is a readonly record struct. Identifiers, labels, titles, descriptions, preset names and manifest fields are checked for shape only: the length and count limits (`MaxCustomLabelLength`, `MaxSections`, `MaxCategories`, the section, category and setting id lengths, `MaxChoices`, `MaxSettings`, `MaxTextLength`, the preset count, `HardwareMatchRule.MaxRules` and `MaxFieldLength`, and the manifest id, text and path lengths) are removed, `PlainText.IsIdentifier` takes no length, `PlainText.TryValidate` gains an overload without one, and `ManifestValidationCode.LimitExceeded` becomes `InvalidText`. `PluginTrace.MaxMessageLength` is removed; trace lines are recorded whole. `IPluginHostAdapter.TraceChange` loses its default implementation, so every host adapter implements it. `CapabilityCommand.ApplyPowerPair` is replaced by `PairedPowerLimitWatts`: the host decides both limits of a declared power pair, every write to either carries the other, and `DevicePowerPair.TryResolve` and `Peer` are new. `CapabilityDescriptor` gains `ProfileScope` (`Switched`, `GlobalOnly`, `NativePerApplication`) and `ApplyTiming` (`Immediate`, `NextApplicationStart`, `SystemRestart`). Both default to the earlier behaviour. |
| 12  | Glyph import keeps only its byte and decode bounds: `GlyphProfileLimits` loses `MaxAssets`, `MaxProfiles`, `MaxControls`, `MaxAliases`, `MaxExactDevices`, `MaxIdentifierLength`, `MaxDisplayNameLength`, `MaxPhysicalLabelLength`, `MaxSvgPaths`, `MaxSvgCommands` and `MaxPathDataLength`, every SVG path is projected, and a PNG text chunk is skipped instead of refused. The host-only `CapabilityStateDelta` and `DeviceSections.IncludePredefined` move into WSGM. `ManifestRules` holds the identity and entry-point rules both manifest readers share: a lowercase package identifier, a canonical version, a `.dll` file name at the package root, an entry type without a backtick, and a plain-text package name. `DeviceRecoveryJournal` is no longer `IAsyncDisposable`, and a plugin's `DisposeAsync` only releases handles after the host awaited `StopAsync`. `DeviceRecoveryJournal.CheckHealthAsync` is removed: opening the record proves the directory writable. `BeginAsync` no longer refuses an unresolved entry: it replaces an entry bound to other firmware and sets an unverified or failed one on the same firmware pending again, and `PendingOriginalFor` is new. `CommandResults.Unverified(command, written)` is removed, because an unverified result carries no readback. `DeviceWriteBudget` and `DeviceWriteBudgetException`, `CapabilityValueValidation`, `CapabilityIds` and `SourceOwnership` are new.                                                                                                                                                                                                                                                                                                                                                |
