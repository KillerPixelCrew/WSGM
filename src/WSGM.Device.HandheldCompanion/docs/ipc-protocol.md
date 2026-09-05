# Handheld Companion IPC — design

Status: **draft, design only** (2026-09-03). Nothing here is implemented yet.

The contract between a running **Handheld Companion** (HC) and a local consumer that reads HC's
state or drives HC's device controls. The first consumer is the WSGM device plugin in this
repository; the server side is a small generic component in
[KillerPixelCrew/HandheldCompanion, branch `IPC`](https://github.com/KillerPixelCrew/HandheldCompanion/tree/IPC),
written to be upstreamed. This document is the single source of truth for both sides.

## 1. The idea in one paragraph

HC does not expose _methods_. It exposes its own **data structures** — the current `PowerProfile`,
the settings dictionary, the device description, the target controller, the sensor readings — as
JSON documents called **roots**, together with a **schema** HC generates at runtime from its own CLR
types. A consumer reads a root, patches a root, and subscribes to a root's changes. HC applies a
patch through the exact code path its own UI uses. Nothing in the HC component names a field, so a
new HC version with new fields needs no IPC change. The only version-sensitive code is the mapping
inside the consumer, and it degrades **per feature**: a field that moved or vanished takes one
capability away, never the link.

## 2. Goals and non-goals

- **Low maintenance.** The HC component is generic over `object` + reflection. Adding a field to
  `PowerProfile` shows up on the wire and in the schema with zero edits.
- **HC stays the hardware owner.** Every write is routed through HC's own update path
  (`UpdateOrCreateProfile`, `SetProperty`, command `Execute`), so every validation, device hook and
  listener HC already has keeps working. Nothing writes hardware behind HC's back.
- **Feature-based degrading, both directions.** Modelled on the WSGM Device SDK: a consumer
  validates each feature's communication against the schema and against the data it receives, and a
  failure makes _that feature_ unavailable with a reason naming the path. The link, the other
  features and the reconnect loop are untouched. HC, in turn, rejects only the offending patch and
  names the path, never the session.
- **Off until enabled, local only.** The pipe exists only while `IPCEnabled` is on in HC's settings.
  A named pipe is unreachable from another machine.
- **No new dependency in HC.** `System.IO.Pipes` and `System.Text.Json` (already referenced).
- Non-goals for v1: remote access, authentication beyond the pipe ACL, controller input streaming,
  layout (remapper) editing, writing the game `Profile`.

## 3. Transport

| Item               | Value                                                                                                                                                                                                                                  |
| ------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Kind               | Windows named pipe, byte mode, duplex, asynchronous, several server instances                                                                                                                                                          |
| Name               | `\\.\pipe\HandheldCompanion.Ipc.v1`                                                                                                                                                                                                    |
| Encoding / framing | UTF-8 without BOM; one JSON document per line, `\n` terminated (NDJSON)                                                                                                                                                                |
| Line limit         | 1 MiB; a longer line ends the connection                                                                                                                                                                                               |
| ACL                | `PipeSecurity` granting _Builtin Users_ ReadWrite (the pattern HC's retired `Shared/Pipes/PipeServer.cs` already used). HC runs elevated; this lets an unelevated consumer of the same machine connect. No _Everyone_, no _Anonymous_. |
| Sessions           | One connection is one session. First message must be `hello`.                                                                                                                                                                          |

## 4. Envelope

```jsonc
// consumer → HC
{"id":12,"op":"get","root":"powerProfile"}
// HC → consumer, answer
{"id":12,"ok":true,"root":"powerProfile","revision":41,"data":{ ... }}
// HC → consumer, refusal
{"id":12,"ok":false,"error":{"code":"unknown-member","path":"/TDPOverrideValue","message":"…"}}
// HC → consumer, push (no id)
{"op":"changed","root":"settings","revision":903,"data":{ ... }}
```

- `id`: positive integer chosen by the consumer, unique per session; every request is answered
  exactly once, in any order.
- Property names on the envelope are camelCase. Property names **inside `data` are HC's own member
  names, unchanged** (`TDPOverrideValues`, `fanSpeeds`, `cTDP`). Applying a naming policy would
  create the one mapping table this design exists to avoid.
- Unknown envelope properties are ignored by both sides, so either side can add optional fields
  without a protocol bump.
- `revision`: per-root monotonic counter HC increments on every change it publishes. A consumer uses
  it to drop stale pushes and to know whether a `get` after a `patch` reflects the patch.

## 5. Operations

| `op`                        | Direction | Purpose                                                                                                                                                                                                                                                                                                         |
| --------------------------- | --------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `hello`                     | →         | Must be first. `{"protocol":1,"client":{"name":"…","version":"…"}}`. Answer: `{"protocol":1,"server":{"name":"HandheldCompanion","version":"0.32.4.0"},"sessionId":"…","roots":["device","settings",…]}`. `sessionId` changes on every HC run; a different id after reconnect means: discard everything cached. |
| `schema`                    | →         | Optional `{"roots":[…]}`. Answer: the schema document (section 7).                                                                                                                                                                                                                                              |
| `get`                       | →         | `{"root":"…"}`. Answer: current document + revision.                                                                                                                                                                                                                                                            |
| `patch`                     | →         | `{"root":"…","data":{…},"transient":false}`. `data` is a **JSON Merge Patch** (RFC 7386) against the root document. Answer: `{"outcome":"applied-verified"\|"applied-unverified","revision":n,"data":{…readback…}}`, or a refusal.                                                                              |
| `invoke`                    | →         | `{"root":"commands","name":"TDPIncrease"}`. One-shot actions that have no data shape. Answer `{"ok":true}` after `Execute` returned.                                                                                                                                                                            |
| `subscribe` / `unsubscribe` | →         | `{"roots":["telemetry","powerProfile"]}`; optional `"intervalMs"` for rate-limited roots. Answer `{"ok":true}`.                                                                                                                                                                                                 |
| `changed`                   | ←         | Push after any change to a subscribed root. Carries the **whole root document** (they are small; a full document is self-correcting where a delta is not).                                                                                                                                                      |
| `goodbye`                   | ←         | HC is exiting or IPC was turned off. The pipe closes right after.                                                                                                                                                                                                                                               |

### 5.1 Patch semantics

1. Validate the merge patch against the root's schema: unknown member → `unknown-member` with the
   JSON pointer; wrong type → `type-mismatch`; member marked read-only → `read-only`. Validation
   fails the patch **as a whole** — a partial apply would leave HC's object in a state its UI never
   produces.
2. Deserialize the patch onto a **clone** of the current object.
3. Hand the clone to the root's apply path (section 6). HC's own code decides whether the values are
   acceptable; a refusal there is `rejected` with `path` when HC can attribute it.
4. Read the object back and answer with it. `applied-verified` when the root re-reads from the
   source of truth (a profile, a setting), `applied-unverified` when HC applied a value it cannot
   read back (some device fan/LED writes).

A patch on `settings` with `"transient": true` maps to `SetProperty(name, value, temporary: true)`:
applied and broadcast, never persisted.

### 5.2 Error codes

| `code`                                         | Meaning                                                                      | Consumer reaction                           |
| ---------------------------------------------- | ---------------------------------------------------------------------------- | ------------------------------------------- |
| `not-ready`                                    | `hello` not done, or HC managers still starting                              | retry later, feature unaffected             |
| `unknown-root`                                 | root not offered by this HC                                                  | degrade every feature on that root          |
| `unknown-member`, `type-mismatch`, `read-only` | patch failed schema validation at `path`                                     | degrade the feature that owns `path`        |
| `rejected`                                     | HC's apply path refused (with optional `path`)                               | report as refused, feature stays available  |
| `unsupported`                                  | device lacks the capability (e.g. `DeviceCapabilities` without `FanControl`) | feature unavailable, reason _Unsupported_   |
| `invoke-failed`, `internal`                    | HC threw                                                                     | report, feature stays available, log detail |
| `version-unsupported`                          | `hello.protocol` not supported                                               | stop; this is the one whole-link refusal    |

## 6. Roots in protocol 1

| Root            | Backing HC object                                                                                                                                                                                                                                                                                                                                                        | Writable                                                                                                          | Change source                                                                       |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------- |
| `device`        | projection of `IDevice.GetCurrent()`: `Capabilities`, `DynamicLightingCapabilities`, `cTDP`, `nTDP`, `GfxClock`, `CpuClock`, `Tjmax`, `BatteryBypassMin/Max/Step`, `LEDPresets`, `BatteryBypassPresets`, `DevicePowerProfiles`, `fanPresets`, `ManufacturerName`, `ProductName`, `ProductModel`, `SystemName`, `SystemModel`, `Processor`, `NumberOfCores`, `DeviceType` | no                                                                                                                | `IDevice.CapabilitiesChanged`                                                       |
| `settings`      | `SettingsManager.GetProperties()` — 142 named values of `bool`, `int`, `double`, `string`, `DateTime`, `StringCollection`                                                                                                                                                                                                                                                | yes, deny-list (section 6.1)                                                                                      | `SettingValueChanged`                                                               |
| `powerProfile`  | `PowerProfileManager.GetCurrent()` — the `PowerProfile` in force (TDP override, GPU/CPU clocks, AutoTDP, cores, parking, boost, `FanProfile`, OEM/OS power mode)                                                                                                                                                                                                         | yes                                                                                                               | `Applied`, `Updated` (when current), `Discarded`                                    |
| `powerProfiles` | every `PowerProfile` in `PowerProfileManager.profiles`                                                                                                                                                                                                                                                                                                                   | v1: select only — `{"current":"<guid>"}` switches the game profile's mapping for the current power line           | `Updated`, `Deleted`                                                                |
| `profile`       | projection of `ProfileManager.GetCurrent()` — the game profile (name, path, enabled, `PowerProfiles` map, `HID`, GPU scaling fields) minus `Layout`, `Icon`, `LibraryEntry`                                                                                                                                                                                              | no (v1)                                                                                                           | `Applied`, `Discarded`, `Updated`                                                   |
| `controller`    | `{"target": projection of ControllerManager.GetTarget().Details + IsPhysical/IsVirtual/IsWireless/IsHidden/UserIndex, "virtual": {"HIDmode","HIDstatus"}}`                                                                                                                                                                                                               | no — HID mode/status are written through `settings` (`HIDmode`, `HIDstatus`), which is how HC's own hotkeys do it | `ControllerManager.ControllerSelected`, `VirtualManager.ControllerSelected`         |
| `telemetry`     | `LibreHardwarePlatform` values: CPU load/clock/power/temperature, GPU load/clock/power/temperature/memory, memory usage/available, battery level/power/time                                                                                                                                                                                                              | no                                                                                                                | the platform's `*Changed` events, coalesced to `intervalMs` (default 1000, min 250) |
| `system`        | `SystemManager`: `PowerLineStatus`, `IsSessionLocked`, `currentSystemStatus`, `IsPowerSuspended`; HC version                                                                                                                                                                                                                                                             | no                                                                                                                | `PowerLineStatusChanged`, `SessionLockChanged`, `SystemStatusChanged`               |
| `commands`      | `FunctionCommands.Functions` — the catalogue of built-in actions (`TDPIncrease`, `HIDStatusCommands`, `ScreenshotCommands`, …) with each command's `IsToggled` where it has one                                                                                                                                                                                          | `invoke` only                                                                                                     | —                                                                                   |
| `glyphs`        | **reserved.** `IController.GetGlyph(ButtonFlags)` + `GetGlyphColor` + `GetButtonName` for the target controller: PromptFont code points per button                                                                                                                                                                                                                       | no                                                                                                                | `ControllerSelected`                                                                |

Every root document carries `observedAt` (UTC ISO 8601) added by the envelope, not by the object.

### 6.1 Settings write scope (open decision, default chosen)

Default in this draft: **all 142 readable; writes denied for** names matching `*Path`, `*Version*`,
`FirstStart`, `FirstRun*`, `HIDInstancePath`, `QuickToolsDevicePath`, `Sentry*`, and anything HC
marks internal. Everything else writable, because HC's listeners already validate values coming from
its own UI through the same call. The alternative is an explicit allow-list, which is safer and more
work to keep current.

### 6.2 Where a write lands

| Consumer intent                         | Root and patch                                                                                            | HC path                                                                                       |
| --------------------------------------- | --------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- |
| Sustained / boost TDP                   | `powerProfile` `{"TDPOverrideEnabled":true,"TDPOverrideValues":[15,15,20]}`                               | `UpdateOrCreateProfile(current, Background)` → device hook → `Applied` → `PerformanceManager` |
| Performance preset                      | `powerProfiles` `{"current":"<guid>"}`                                                                    | game profile `PowerProfiles[powerLine] = guid` → `ProfileManager.UpdateOrCreateProfile`       |
| Fan mode / curve                        | `powerProfile` `{"FanProfile":{"fanMode":"Software","fanSpeeds":[…]}}`                                    | same as TDP; `PowerProfileManager` fan watchdog                                               |
| Lighting                                | `settings` `{"LEDSettingsEnabled":true,"LEDBrightness":80,"LEDMainColor":"#FF6A00","LEDSettingsLevel":1}` | `SetProperty` → `DynamicLightingManager`                                                      |
| Charge limit                            | `settings` `{"BatteryChargeLimit":true,"BatteryChargeLimitPercent":80}`                                   | `SetProperty` → device subclass listener                                                      |
| Virtual controller on/off               | `settings` `{"HIDstatus":1}`                                                                              | `SetProperty` → `VirtualManager`                                                              |
| Take a screenshot, cycle sub-profile, … | `invoke` on `commands`                                                                                    | `ICommands.Execute(true, true, true)`                                                         |

## 7. Schema

HC generates the schema at runtime with `System.Text.Json.Schema.JsonSchemaExporter` (STJ 10 is
already referenced) from the same `JsonSerializerOptions` it serializes the roots with. That is what
makes the schema self-defining: it is derived from the exact types in the running build.

```jsonc
{
  "protocol": 1,
  "server": {"name":"HandheldCompanion","version":"0.32.4.0"},
  "roots": {
    "powerProfile": {
      "writable": true,
      "transient": false,
      "revision": 41,
      "schema": { "$schema":"https://json-schema.org/draft/2020-12/schema", "type":"object",
                  "properties": { "TDPOverrideEnabled":{"type":"boolean"},
                                  "TDPOverrideValues":{"type":["array","null"],"items":{"type":"number"}},
                                  "FanProfile":{"type":"object","properties":{"fanMode":{"enum":["Hardware","Software"]},"fanSpeeds":{"type":"array","items":{"type":"number"}}, … }},
                                  … } }
    },
    "settings": {
      "writable": true, "transient": true, "revision": 903,
      "schema": { "type":"object", "properties": { "LEDBrightness":{"type":"integer"}, "HIDmode":{"type":"integer"}, … },
                  "x-readOnly": ["HIDInstancePath", …] }
    },
    "device": { "writable": false, … },
    …
  }
}
```

Serializer options, identical on both sides of the schema and the data:

- `IncludeFields = true` (HC's models mix public fields and properties).
- Enums as strings (`JsonStringEnumConverter`) so the schema carries their names; `[Flags]` enums
  serialize as a comma-separated string, and the schema marks them `x-flags: true`.
- **No naming policy.**
- A single `DefaultJsonTypeInfoResolver` modifier removes members whose type is not _projectable_:
  delegates and events, interfaces and abstract classes (e.g. `Layout`'s action dictionaries), WPF
  and WinForms types (`ImageSource`, `Color` is kept and written as `#RRGGBB`), handles and
  pointers, `Task`, and anything under `System.Windows.*`, `LibreHardwareMonitor.*`, `hidapi.*`.
  This one filter is what lets `IDevice`, `IController` and `Profile` be exposed without a
  hand-written DTO.
- `[Obsolete]` members are exported and flagged `x-obsolete: true` so a consumer prefers the
  replacement when both exist.

## 8. Feature-based degrading (consumer contract)

A consumer never treats the schema as pass/fail. It declares **features**, each with the paths it
needs and the shape it expects, validates every feature independently on `hello`, and keeps
validating on every document it receives:

```text
feature power.sustained-limit
  requires powerProfile /TDPOverrideEnabled : boolean, writable
           powerProfile /TDPOverrideValues  : array<number> length ≥ 2, writable
           device       /cTDP               : array<number> length 2
  degrades to: capability unavailable, reason PrerequisiteMissing, detail "powerProfile/TDPOverrideValues"

feature lighting.brightness
  requires settings /LEDBrightness : integer, writable
           device   /DynamicLightingCapabilities contains "…"  (flags string)
           device   /Capabilities contains "DynamicLightingBrightness"

feature telemetry.cpu-temperature
  requires telemetry /CPUTemperature : number|null
```

Rules:

1. **Schema mismatch degrades one feature.** Missing path, wrong type, or `read-only` on a path the
   feature writes → that feature is unavailable with the failing path as the detail. The link,
   subscriptions and all other features continue.
2. **Data mismatch degrades one feature.** A document whose field cannot be parsed into the
   feature's expected shape marks that feature unavailable until a later document parses again.
3. **A patch refusal degrades one feature** when the error is `unknown-member`, `type-mismatch` or
   `read-only`; a `rejected` is reported as a refused command and the feature stays available.
4. **The only whole-link refusals** are `version-unsupported` and a broken wire format. Both surface
   as _HostUnavailable_ on every feature while the consumer keeps reconnecting.
5. **`sessionId` changed** → re-run every feature's validation; HC may have been updated.

In the WSGM plugin this maps one-to-one onto the Device SDK: one feature is one
`CapabilityDescriptor`; an unavailable feature publishes
`CapabilityState { Available = false, Reason = PrerequisiteMissing(detail: path) }`; descriptor
generation is bumped only when the set of _declared_ features changes (a `sessionId` change), not
when one flips availability.

## 9. HC-side component (for the `IPC` branch)

Small and generic; nothing in it names a `PowerProfile` member.

| File                     | Role                                                                                                                                                                                                                                                 |
| ------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `IPC/IpcServer.cs`       | pipe listener, one `IpcSession` per client, NDJSON read/write, `hello` gate, request dispatch, push fan-out                                                                                                                                          |
| `IPC/IpcRoot.cs`         | `abstract class IpcRoot { string Name; bool Writable; JsonNode Read(); PatchResult Patch(JsonObject merge, bool transient); event Changed; }` plus `ObjectRoot<T>` (clone → merge → apply delegate), `SettingsRoot`, `TelemetryRoot`, `CommandsRoot` |
| `IPC/IpcProjection.cs`   | the shared `JsonSerializerOptions`, the projectable-type filter, `#RRGGBB` colour converter                                                                                                                                                          |
| `IPC/IpcSchema.cs`       | `JsonSchemaExporter` over every root, `x-readOnly` / `x-flags` / `x-obsolete` annotations                                                                                                                                                            |
| `Managers/IpcManager.cs` | `IManager`; registers roots, subscribes HC events, starts the server when `IPCEnabled` is true, stops on `Stop`/`Suspend`                                                                                                                            |
| Settings                 | `IPCEnabled` (bool, default **false**) in `Settings.settings`, `Settings.Designer.cs`, `App.config`; a `SettingsExpander` on `SettingsPage` next to the DSU server card, same wiring as `DSUEnabled`                                                 |

Threading: HC raises its events from arbitrary threads; roots snapshot under a lock and the server
serializes writes per session. Patches run on the thread pool and call HC's update methods the way
its hotkey commands do (they are already called from `InputsManager` threads).

## 10. WSGM plugin side (this repository)

- Detection: matches when HC is installed (uninstall registry entry or a running `HandheldCompanion`
  process) or the pipe exists — no mutable resource is touched.
- Start: connect with backoff (1 s → 30 s), `hello`, `schema`, validate features, `get` each root,
  publish descriptors and states, `subscribe` to every used root with telemetry at 2 s. If HC is not
  reachable inside the start budget: publish one diagnostics row ("Waiting for Handheld Companion"),
  return _Degraded_ with a retryable _PrerequisiteMissing_, keep reconnecting.
- Commands: one capability → one patch or invoke; outcome mapping `applied-verified` →
  `AppliedVerified` with readback, `applied-unverified` → `AppliedUnverified`, refusal → `Rejected`
  with the mapped reason, disconnect mid-flight → `Indeterminate`.
- The plugin publishes **no** physical devices, controller samples or haptic sink, and declares
  `ControllerOwnership.External` (section 12): HC owns the physical controller _and_ the virtual one
  while it runs, and WSGM must not stand up its own controller backend beside it.
- Stop restores nothing: every write went through HC's own persisted user intent, exactly as if the
  user had used HC's Quick Tools, and undoing it on WSGM exit would surprise them.

Feature → capability mapping (initial set):

| Feature                                       | Root / paths                                                                                                                                            | SDK role · value · unit                                                                                    |
| --------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------- |
| `power.sustained-limit`                       | `powerProfile/TDPOverrideValues[0]` (+`[1]`), bounds `device/cTDP`                                                                                      | `PowerSustainedLimit` · Integer · Watt                                                                     |
| `power.fast-limit`                            | `powerProfile/TDPOverrideValues[2]`                                                                                                                     | `PowerFastLimit` · Integer · Watt                                                                          |
| `power.profile`                               | `powerProfiles` list, `powerProfile/Guid`                                                                                                               | `ScenarioMode` · Choice                                                                                    |
| `fan.mode`                                    | `powerProfile/FanProfile/fanMode`, gate `device/Capabilities ∋ FanControl`                                                                              | `FanMode` · Choice (Hardware/Software)                                                                     |
| `fan.curve`                                   | `powerProfile/FanProfile/fanSpeeds` (11 points, 0–100 °C)                                                                                               | `FanCurve` · Curve                                                                                         |
| `charge.limit`                                | `settings/BatteryChargeLimit*`, bounds `device/BatteryBypassMin/Max/Step`, gate `Capabilities ∋ BatteryChargeLimit`                                     | `ChargeLimit` · Integer · Percent                                                                          |
| `charge.bypass`                               | `settings/BatteryBypassChargingMode`, `device/BatteryBypassPresets`                                                                                     | `ChargeBypass` · Choice                                                                                    |
| `lighting.power/brightness/zone/effect/speed` | `settings/LEDSettingsEnabled, LEDBrightness, LEDMainColor, LEDSecondColor, LEDSettingsLevel, LEDSpeed`, gates from `device/DynamicLightingCapabilities` | `LightingPower` · `LightingBrightness` · `LightingZoneColor` ×2 · `LightingEffect` · `LightingEffectSpeed` |
| `telemetry.*`                                 | `telemetry/CPUTemperature, CPUPower, GPUTemperature, BatteryLevel`                                                                                      | `Telemetry` · Integer                                                                                      |
| `controller.target`, `controller.virtual`     | `controller/target/Name`, `controller/virtual/HIDmode`                                                                                                  | `GenericReadOnly` · Text                                                                                   |
| `hc.link`                                     | envelope                                                                                                                                                | `GenericReadOnly` · Text (connection state, HC version)                                                    |

## 11. Open decisions

1. **Settings write scope** — deny-list (this draft) or allow-list.
2. **Where `powerProfile` writes go** — the current profile (this draft, matches Quick Tools) or a
   dedicated external profile HC creates and applies while a consumer is connected.
3. **`profile` writability** — v1 read-only; writing the `PowerProfiles` map is the first likely
   need.
4. **`commands`** — keep as `invoke`, or drop for v1 to keep the surface pure data.
5. **`glyphs`** — HC's side is trivial; the WSGM SDK can only take glyphs as static package files
   (SVG/PNG by SHA-256) today, so consuming them needs a build-time render of PromptFont into a
   glyph package or an SDK addition for plugin-published profiles. Decide after the rest works.
6. **Naming** — `IPCEnabled` and the pipe name are placeholders until the HC maintainer has a
   preference.

## 12. Required SDK extension: controller ownership

**Rule:** while the Handheld Companion plugin is the installed device package, **HC is the
controller owner.** WSGM does not create a virtual target, does not hide anything, does not offer
controller management, and reads its own UI input from whatever controller HC presents, through the
same SDL-plus-Steam-Input-lease path it uses whenever management is not active.

Today WSGM cannot be told this. What the 2.0 host does when a plugin simply publishes nothing
(verified in `src/WSGM/Shell`, 2026-09-03):

| Host behaviour                                                                                                                                               | Where                                                                                                         | Effect with HC present                                                                                                                                                                            |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Virtual target + WSGM's HidHide delta start only from `PublishPhysicalDevicesAsync`                                                                          | `DeviceCoordinator.OnPhysicalIdentities` → `ControllerManager.StartAsync`                                     | Correct by accident: never started.                                                                                                                                                               |
| Before every cycle start WSGM adds itself to HidHide's **application allowlist** when controller management is on in config                                  | `DeviceCoordinator` → `ControllerManager.EnsureHidHideReadableAsync` → `HidHideOwnership.EnsureReadableAsync` | **Wrong.** HC hides the physical pad and allowlists only itself; WSGM allowlisting itself makes its SDL input see the physical pad _and_ HC's virtual one — duplicate input inside WSGM's own UI. |
| Settings shows the _Controller management_ toggle; the overlay's Controller rows show "Ready · no virtual controller is present yet" and a selectable target | `SettingsViewModel.DeviceControllerManagementEnabled`, `DeviceOverlayBridge`                                  | **Wrong.** Offers a target that must never exist; misleading state text.                                                                                                                          |
| `PluginStartContext.ControllerManagementEnabled`, `SetControllerManagementAsync`, `ReleaseControllerAsync`, `ApplyHapticOutputAsync` are all driven          | `DevicePluginRuntime`                                                                                         | Harmless no-ops in the plugin, but the host asks questions that have no meaning.                                                                                                                  |
| UI input source falls back to SDL with the Steam Input lease whenever management is not active                                                               | `ControllerManager.UiSource`                                                                                  | Correct and required: this is how WSGM's overlay reads HC's virtual controller.                                                                                                                   |

### 12.1 Proposed contract (`WSGM.Device.Sdk`)

```csharp
namespace WSGM.Device.Sdk.Plugin;

/// <summary>Who owns the physical controller and any virtual controller while this package runs.</summary>
public enum ControllerOwnership
{
    /// <summary>The plugin acquires the physical controller and WSGM drives a virtual target (today's behaviour).</summary>
    Plugin,

    /// <summary>
    /// An external manager owns both the physical controller and its virtual replacement.
    /// WSGM creates no target, touches no HidHide state — not even its own readability
    /// allowlist — offers no controller management, and navigates its own surfaces from the
    /// controller the external manager presents.
    /// </summary>
    External,

    /// <summary>
    /// Nobody manages a controller for this package: pads talk to Steam directly, as on an
    /// ordinary desktop. Same host behaviour as <see cref="External"/> with no owner to name.
    /// For an integration that leaves the existing controller stack untouched.
    /// </summary>
    Unmanaged,
}

public interface IDevicePlugin
{
    /// <summary>Declared once per package; read after load and before any lifecycle call.</summary>
    ControllerOwnership ControllerOwnership => ControllerOwnership.Plugin;

    /// <summary>Bounded plain-text name of the external owner for WSGM's surfaces, or null.</summary>
    string? ExternalControllerOwnerName => null;
    // …existing members unchanged…
}
```

- A **default interface member**, so every existing plugin keeps compiling and behaving; the
  proposal targets the current SDK API level 3. Whether it needs a new API level must be decided
  when the extension is implemented and its host behavior is reviewed.
- A **type-level declaration, not a publication**: it must be known _before_
  `EnsureHidHideReadableAsync`, which runs before `StartAsync`, and it never changes during a cycle.
  The manifest is the wrong place (it is deliberately six fields and carries no capability facts); a
  property on the loaded plugin type is read by `PluginPackageLoader` right after the entry type is
  instantiated.
- `ExternalControllerOwnerName` goes through `PlainText.TryValidate` (48 chars) like every other
  plugin-supplied label. The HC plugin returns `"Handheld Companion"`.

### 12.2 Host behaviour for `External`

1. `DeviceCoordinator` skips `EnsureHidHideReadableAsync` and never calls
   `ControllerManager.StartAsync`; `ControllerManagementState` becomes a new `ExternallyOwned` state
   carrying the owner name, so logs and the overlay say "Controller: managed by Handheld Companion"
   instead of "Ready · no virtual controller is present yet".
2. Settings hides the _Controller management_ toggle and the target pickers while such a package is
   installed, with the same owner-name line. The stored config value is left untouched so a later
   Claw install gets it back.
3. `PluginStartContext.ControllerManagementEnabled` is `false`; the host does not call
   `SetControllerManagementAsync`, `ReleaseControllerAsync` or `ApplyHapticOutputAsync`, and a
   publication of physical devices or controller samples from such a plugin is refused and logged as
   a plugin defect rather than starting management anyway.
4. UI input stays on `UiInputSource.SdlWithSteamLease`. Nothing new is needed there; the lease is
   scoped to WSGM's own surfaces and does not interfere with HC's virtual controller.
5. Glyphs: no physical glyph profile is selected (there is no WSGM-owned controller). Steam keeps
   the glyphs it derives from HC's virtual controller type. This is the point where the reserved
   `glyphs` root (section 6) would feed HC's PromptFont artwork in later, if the SDK grows a
   plugin-published glyph source.

### 12.3 Why not "just publish nothing"

Because the allowlist side effect above happens regardless, and because a rule that exists only as
the absence of a call cannot be seen in a log, a settings page or a test. The Device SDK's own
principle applies: what the host relies on must be declared, not inferred.

## 13. Versioning

`protocol` is one integer. The schema _is_ the version of the data: consumers validate against it,
never against a release number. Additive changes to the envelope do not bump `protocol`; changing
the meaning or type of an envelope field does. HC's own model changes never bump it — that is the
point.
