# MSI Claw 8 AI+ A2VM Device Plugin Plan

Status: implementation-ready design, first hardware capture recorded, validation ongoing  
Target branch: `2.0`  
Parent plan: [`2.0-design.md`](./2.0-design.md)  
Reference device: MSI Claw 8 AI+ A2VM, board `MS-1T52`  
Research snapshot: 2026-08-26  
First hardware capture: 2026-08-27, read-only enumeration on the reference unit (this development
machine). Findings from that capture are marked **[HW 2026-08-27]** throughout and supersede any
claim inherited from Handheld Companion, HHD, the Linux series, or ClawTweaks.

## Purpose

This document defines the first complete WSGM 2.0 device plugin. It covers detection, ownership, power, fans, lighting, the physical controller, rumble, motion sensors, rear buttons, front OEM buttons, and the MSI firmware-generated `Win+G` shortcut on the right Quick Settings button.

Handheld Companion is a behavioral and protocol reference, not the architecture to reproduce. Where possible, its findings have been checked against the accepted Linux `hid-msi` driver series, HHD, the Linux `msi-wmi-platform` work, ClawTweaks, and MSI's published device specification. Conflicts are called out instead of being silently resolved in favor of one implementation.

Device ownership follows the WSGM process lifetime, not the current shell mode. When integration is enabled, the plugin starts asynchronously with WSGM and remains active across Desktop Mode, Game Mode, games, and transitions between them. The WSGM overlay is the complete device-control surface in both Desktop and Game Mode; Steam's native QAM is an additional Game Mode surface.

Asynchronous initialization means only that hardware work does not block WSGM startup, its UI, or a Desktop/Game Mode transition. It does not mean initialization is deferred until Game Mode. If WSGM is launched on the desktop, the Claw is detected and controllable there immediately as each capability becomes ready.

## Locked product decisions

- The plugin targets the Claw 8 AI+ A2VM board `MS-1T52`. It must not use the Claw A1M's limits or firmware offsets.
- Device integration is optional. When it is disabled, no Claw host, hook, sensor subscription, WMI watcher, HID handle, firmware write, virtual controller, or HidHide change remains active.
- When device integration is enabled, its lifecycle spans the entire WSGM run. Entering or leaving Game Mode does not activate, deactivate, reset, or hand off the device.
- The device lifecycle has exactly two terminal triggers: WSGM exits, or the user turns Device Integration off in Settings during the run.
- The Steam Input lease subsystem remains a permanent WSGM capability. Managed device input changes when surface leases are acquired; it does not remove the lease implementation.
- Controller management is independently optional beneath device integration. This permits WSGM hardware control with Handheld Companion or another application owning controller emulation.
- HIDMaestro remains the WSGM virtual-controller backend. The Claw plugin emits canonical input and consumes canonical output; it never talks to HIDMaestro directly.
- Initial virtual targets are Steam Deck Composite, Xbox 360, and DualShock 4.
- There is no general input mapper and no gyro-to-mouse or gyro-to-stick mapping. Only OEM controls may be reassigned.
- Every user-facing device control lives in the WSGM overlay. Settings contains only WSGM ownership and startup configuration.
- The right-front Quick Settings button opens Steam's native QAM by default.
- While the Claw plugin owns OEM-button capture and suppression, it blocks the confirmed firmware `Win+G` side effect without disabling `i8042prt`, the ACPI keyboard device, or the volume keys; WSGM maps the resulting logical control to QAM.
- MSI WMI and the controller's vendor HID protocol are the A2VM transports. PawnIO/direct EC access is not needed for this plugin unless future hardware evidence proves otherwise.
- The Claw plugin owns every MSI WMI, vendor HID, power, fan, lighting, controller, motion, rumble, OEM-input, validation, rollback, and recovery implementation. WSGM exposes semantic capabilities and UI but no Claw hardware backend.
- The exact `MS-1T52` definition composes reusable MSI implementation modules; it never inherits another Claw model's limits or firmware policy.
- Unknown board IDs, unknown controller firmware, failed prerequisite reads, and ownership conflicts degrade capabilities; they never trigger guessed hardware writes.

## Scope and non-goals

### In scope

- Exact device and firmware detection.
- Serialized MSI WMI access for sustained and short power limits, fan tables, fan mode, fan speed, and relevant telemetry.
- Serialized vendor HID access for controller mode, configuration/profile reads, lighting, and verified output reports.
- DirectInput/XInput physical input acquisition.
- M1 and M2 rear-button acquisition.
- Physical dual-motor rumble.
- Windows motion-sensor acquisition, orientation correction, and calibration.
- WMI front-button events and firmware keyboard-chord suppression.
- Overlay controls, native Steam QAM integration, profiles, diagnostics, lifecycle recovery, and clean external-manager handoff.

### Explicitly out of scope

- Face-button, stick, trigger, or D-pad remapping.
- Gyro-to-mouse, gyro-to-stick, or touch-to-mouse conversion.
- Inventing touchpads or stick-touch sensors that the Claw does not have.
- Arbitrary keyboard macros, executable actions, or scripts in OEM-button assignments.
- Disabling the PS/2/i8042 service.
- Killing MSI Center M, Handheld Companion, or their services automatically.
- A new unsigned kernel input filter for the first release.
- Direct EC register experimentation on users' devices.
- Repeated firmware/EEPROM writes during startup, preview, polling, or every lighting frame.

## Evidence and confidence model

Implementation work uses the evidence lifecycle defined by Device Lab:

| State | Meaning | Write policy |
| --- | --- | --- |
| Candidate | Suggested by a reference, similarity match, schema, or one observation | No generated or runtime write |
| Correlated | Repeatedly associated with the expected action but not yet causally proven | No generated or runtime write |
| Corroborated | Supported by repeated A/B/revert evidence or independent sources | Eligible only for an explicit bounded compatibility trial |
| Hardware verified | Reproduced on the exact target by a reviewed bounded Device Lab trial or the Claw plugin, with readback and verified restoration | Eligible for implementation and acceptance testing |
| Retail approved | Reviewed against supported firmware, lifecycle, failure, and safety gates | May ship |
| Rejected | Disproven, unsafe, incompatible, or superseded | Must not be selected |

Provenance is recorded separately from evidence state: official documentation, independently captured hardware evidence, another open-source implementation, HC behavioral reference, or inference. A mature reference does not automatically make a claim hardware verified on `MS-1T52`.

The first hardware bring-up records the user's exact SMBIOS data, controller `bcdDevice`, HID descriptors, report descriptors, PnP/container IDs, MSI WMI provider version, BIOS/EC/controller firmware versions, motion-sensor identity, WMI event timing, and current hardware state. That capture becomes the first golden fixture for the plugin.

## Device identity and activation gate

The activation gate is intentionally narrower than a generic MSI VID check.

| Signal | SMBIOS table/field | Required value |
| --- | --- | --- |
| Manufacturer | Type 1 / `Win32_ComputerSystem.Manufacturer` | Normalized/case-insensitive `Micro-Star International Co., Ltd.` |
| Board product | Type 2 / `Win32_BaseBoard.Product` | `MS-1T52` |
| System product | Type 1 / `Win32_ComputerSystem.Model` | `Claw 8 AI+ A2VM` — display/weak evidence only |
| System SKU | Type 1 / `Win32_ComputerSystem.SystemSKUNumber` | `1T52.1` |
| System family | Type 1 / `Win32_ComputerSystem.SystemFamily` | `Claw` — coarse family predicate only |
| Controller vendor ID | — | `0x0DB0` |
| Normal controller product IDs | — | `0x1901` XInput or `0x1902` DirectInput |
| Additional observed IDs | — | `0x1903` and `0x1904`, diagnostics only until mode semantics are confirmed |
| WMI scope | — | `root\\WMI` |
| MSI ACPI provider | — | Enumerated `MSI_ACPI` instance validated against the board/provider version; the instance path must be discovered, never hardcoded |

**[HW 2026-08-27] The board and system products are two different SMBIOS fields.** Earlier revisions
of this plan wrote "SMBIOS product/board `MS-1T52`" as one signal. On the reference unit `MS-1T52`
is the *baseboard* product; the *system* product is the marketing string `Claw 8 AI+ A2VM`. A
matcher that reads "SMBIOS product" as Type 1 never matches this device. Every identity predicate
must name its exact table and field.

`SystemSKUNumber` and `SystemFamily` are newly observed and are both better machine-readable
signals than the marketing name.

Captured reference values:

| Field | Value |
| --- | --- |
| BIOS version | `E1T52IMS.112` (`MSI_NB - 1072009`), released 2025-04-12 |
| CPU | Intel Core Ultra 7 258V (Lunar Lake), Family 6 Model 189 Stepping 1, 8C/8T |
| GPU | Intel Arc 140V |
| OS at capture | Windows 11 Home 10.0.26200 |
| Controller `bcdDevice` | `0x0229` — see the RGB firmware gate |

**[HW 2026-08-27] EC firmware version comes from `Get_EC`, not SMBIOS.**
`Win32_BIOS.EmbeddedControllerMajorVersion` and `…MinorVersion` both return `255` (`0xFF`), the
SMBIOS "unknown" encoding — so SMBIOS is a dead end. The provider answers it directly: `Get_EC`
returns an ASCII string after a status byte and one `0x81` marker byte:

```
01 81 "1T52EMS1.109" "12042025" "09:10:47"
```

giving **EC firmware `1T52EMS1.109`, built 2025-04-12 09:10:47**. The EC version can therefore stay
an identity gate, sourced from the provider rather than SMBIOS. Note the embedded `1T52` board tag,
which independently corroborates the board match.

`MS-1T42` is the 7-inch Claw A2VM and `MS-1T41` is the A1M. They require separate plugin descriptors even when a transport is shared. In particular, the A1M's larger power limits must never leak into the `MS-1T52` descriptor.

The exact board match activates device discovery even if the controller is disabled, offline, or re-enumerating. Each capability has its own secondary gate:

- Power and fan control require a responsive MSI WMI provider and successful snapshot reads.
- Lighting and firmware-profile operations require an exact controller-firmware descriptor.
- Controller ownership requires a known controller mode and a successful re-enumeration test.
- Motion requires a stable Windows sensor identity.
- Front OEM actions require captured MSI WMI events. The firmware chord may be suppressed, but its low-level-hook observation is not trusted as an action source because the hook cannot identify the originating keyboard.

An unknown `bcdDevice` may still allow ordinary gamepad input and read-only diagnostics. It must not select the "nearest" RGB or profile-memory address.

## Proposed plugin manifest

The exact 2.0 package schema remains a core-platform decision, but the Claw package should be expressible without code outside the device package. A proposed declarative portion is:

```json
{
  "id": "wsgm.device.msi.claw-8-a2vm",
  "apiVersion": 1,
  "displayName": "MSI Claw 8 AI+ A2VM",
  "devices": [
    {
      "smbiosManufacturer": "MICRO-STAR INTERNATIONAL CO., LTD.",
      "smbiosBaseboardProducts": ["MS-1T52"],
      "smbiosSystemSku": "1T52.1",
      "usb": [{ "vid": "0DB0", "pids": ["1901", "1902", "1903", "1904"] }]
    }
  ],
  "resources": [
    { "id": "msi-acpi", "kind": "wmi", "access": "read-write" },
    { "id": "claw-mcu", "kind": "hid", "access": "read-write" },
    { "id": "physical-controller", "kind": "controller", "access": "exclusive-when-managed" },
    { "id": "motion", "kind": "windows-sensor", "access": "read" },
    { "id": "oem-chord", "kind": "interactive-keyboard-hook", "access": "suppress" }
  ],
  "riskDeclarations": [
    "hardware-power-writes",
    "custom-fan-control",
    "controller-reenumeration",
    "device-persistent-lighting-possible",
    "global-keyboard-suppression"
  ],
  "dependencies": [{ "id": "msi-wmi-provider", "kind": "oem-installed", "required": true }],
  "implementationModules": [
    { "id": "MsiWmiPlatform", "version": 1 },
    { "id": "MsiClawMcu", "version": 2 },
    { "id": "MsiClawA2VmPowerPolicy", "version": 1 }
  ],
  "capabilities": [
    "power-limits",
    "dual-fan-curves",
    "rgb",
    "physical-controller",
    "rumble",
    "motion",
    "oem-buttons"
  ]
}
```

The manifest is auditable metadata, not authorization by assertion. Resource and risk declarations describe what the package intends to use; they cannot constrain direct user-mode access by arbitrary plugin code. WSGM verifies package trust and declarative install-time prerequisite metadata before activation. The Claw plugin authoritatively probes the MSI provider and runtime dependency health during activation and reports per-capability availability. No privileged helper is declared unless hardware testing proves one necessary and that exact Claw-specific component receives a separate review.

## Runtime architecture

WSGM's main executable remains NativeAOT. The Claw integration needs dynamic community plugins, `System.Management`/WMI, WinRT sensors, and an interactive keyboard hook, so the plugin does not load into the main process.

```mermaid
flowchart LR
    A["Overlay and Steam QAM"] <--> B["WSGM semantic router"]
    B <--> C["DeviceHost"]
    C <--> D["Claw plugin modules"]
    D <--> E["WMI, HID, sensors, input"]
```

### Process boundary

`WSGM.DeviceHost.exe` is a JIT-capable, unelevated, per-user, per-interactive-session sidecar. Each plugin package runs in its own host process and receives no WSGM secrets, unrelated handles, or privilege-bearing client. The host loads the Claw package; the plugin owns WMI, HID, WinRT sensors, the interactive hook, every hardware state machine, and its recovery journal.

Process separation provides crash and dependency isolation. It is not described as a security sandbox.

The sidecar starts during WSGM startup when device integration is enabled and stays alive until WSGM exits or the user turns Device Integration off in Settings. Steam/Big Picture state, games starting/stopping, controller-management selection, individual resource conflicts, and plugin capability health are not host-lifetime decisions.

**[HW 2026-08-27] MSI WMI requires elevation.**

`Get-CimInstance -Namespace root\WMI -ClassName MSI_ACPI` fails with `WBEM_E_ACCESS_DENIED` from a
medium-integrity process. The same call against `MSAcpi_ThermalZoneTemperature` — independently
known to require administrator — fails with the identical error, while `BatteryStatus` and
`MSI_Event` succeed unelevated. The failure is an access check, not a missing provider.

This is not a problem to engineer around. WSGM already runs elevated by design and de-elevates what
does not need privilege, so DeviceHost elevation is a spawn decision, not a new component:

- **Reviewed first-party packages (the Claw plugin) run elevated**, inheriting the privilege WSGM
  already holds. MSI WMI works directly. No helper executable, no broker, no IPC hop.
- **Untrusted and community packages are spawned de-elevated** and simply do not get WMI-backed
  capabilities. The package trust tier already in the design is the boundary that decides this.

Earlier revisions of this plan asserted a blanket unelevated DeviceHost. That rule bought little —
the same documents concede process separation "is not a malware sandbox" — while costing the entire
MSI WMI transport. Replace it with the trust-tiered spawn decision above; keep the *isolation*
rationale (crash and dependency containment), drop the *privilege* rationale for reviewed packages.

`MSI_Event`, the OEM button source, reads fine unelevated. OEM input and the firmware-chord
suppressor therefore do not depend on this at all.

**[HW 2026-08-27] The provider *schema* is readable unelevated; only instances are denied.** Measured
by running the Device Lab inventory sweep twice on this unit, once medium-integrity and once
elevated:

| `root\WMI` class | Unelevated | Elevated |
| --- | --- | --- |
| `MSI_ACPI` | **Access denied** on instance enumeration, but the class definition and all **38 method signatures** read fine | Available, 1 instance |
| `MSI_Event` | Available | Available |
| `BatteryStatus` | Available | Available |
| `MSAcpi_ThermalZoneTemperature` | **Access denied** (independent control — known to require admin) | Available, 1 instance |

The thermal-zone class is included as a control precisely so this distinction is provable rather than
assumed: it denies and succeeds in lockstep with `MSI_ACPI`, which confirms the boundary is an access
check on instances and not something specific to the MSI provider.

This matters for the trust-tiered spawn decision. A **de-elevated** host can still determine that the
provider is present and that it declares the exact methods a module needs, so an untrusted package's
capability list can be *accurate* — reporting WMI-backed capabilities unavailable with a
`prerequisite-missing` versus an access reason correctly — without any elevation. Only reading and
writing values needs it. Detection and availability reporting must therefore not be gated behind
elevation, or a de-elevated package will misreport a present provider as missing.

The input hook cannot live in a Session 0 service. It runs on the logged-in user's desktop.

**[HW 2026-08-27] The elevated-foreground concern largely dissolves.** Earlier revisions worried
that a medium-integrity hook could not neutralize the chord over an elevated foreground process, and
`P0-006` was raised as a blocking decision because of it. Since the reviewed Claw host runs elevated
along with the rest of WSGM, its `WH_KEYBOARD_LL` hook is installed from an elevated process and is
not outranked by elevated foreground windows. `P0-006` reduces to ordinary validation rather than an
architectural choice between three unpleasant options. Secure desktop remains out of scope, and
desktop/session notifications still reset hook state.

### IPC

Use named pipes, not localhost TCP.

The control pipe has:

- A current-user SID ACL.
- Per-session endpoint naming and authentication material.
- Protocol-version negotiation.
- Bounded binary messages.
- Request IDs, timeouts, cancellation, and idempotency keys.
- Capability snapshots and delta events.
- Explicit host liveness and generation IDs.
- No generic execute-command operation.

High-rate controller and IMU samples must not be serialized as JSON RPC. Use a fixed binary shared-memory state page or bounded ring buffer with sequence counters and an event signal. The named pipe remains the control, lifecycle, diagnostics, and low-rate event plane. Output rumble may use a bounded return ring or compact pipe messages if measurement shows that it is cheap enough.

Steam CEF receives only allowlisted WSGM commands such as `SetTdp`, `SetFrameLimit`, or `SelectControllerTarget`. It never receives the device pipe, a plugin object, raw WMI/HID access, or a plugin-helper endpoint.

### Transport split

The plugin is composed from independently testable implementation modules:

| Component | Responsibility |
| --- | --- |
| `MsiWmiPlatform` | Serialized 32-byte named WMI transactions and status validation |
| `ClawA2VmPowerCapability` | Exact `MS-1T52` limits, scenario policy, ordering, readback, and rollback |
| `ClawA2VmFanCapability` | Exact `MS-1T52` channels, tables, RPM, safety policy, and firmware release |
| `MsiClawMcu` | Serialized 64-byte vendor HID requests, ACK matching, profiles, mode switches, and RGB framing |
| `ClawA2VmLightingCapability` | Exact firmware/profile gate, physical zone layout, volatile/persistent policy, and rollback |
| `ClawControllerSource` | DirectInput/XInput acquisition and normalized physical input |
| `ClawRumbleSink` | Validated live motor output and stop-on-failure behavior |
| `ClawMotionSource` | Windows gyrometer/accelerometer binding, timestamps, transforms, and calibration |
| `ClawOemInputSource` | MSI WMI event codes, M1/M2 events, deduplication, and logical OEM-event publication |
| `FirmwareChordSuppressor` | The narrowly scoped firmware `Win+G`/`Win+Tab` neutralizer |

`UiInputArbiter` remains a WSGM-owned consumer of canonical plugin input. WSGM maps logical OEM events to allowlisted actions. No plugin module performs UI work, and no hook callback performs WMI, HID, IPC, logging, allocation-heavy work, or action dispatch.

## Lifecycle and ownership

### States

| State | Meaning |
| --- | --- |
| Disabled | Integration is off; no plugin process or resource is active |
| Detected | Exact board found; capabilities are still being probed |
| Passive | Hardware exists but another active writer/owner or a missing prerequisite prevents one or more resources from being owned |
| Activating | Snapshots and asynchronous resource acquisition are in progress |
| Active | At least one capability is owned and healthy |
| Degraded | Some capabilities failed or are unavailable; healthy capabilities remain usable |
| Suspended | Writes, samples, rumble, and hooks are quiesced for sleep/session transition |
| Deactivating | New commands are rejected and owned state is being released/restored |

### Activation order

Activation starts with WSGM, happens in the background, and is cancellable:

Plugin-owned activation:

1. Confirm the exact SMBIOS board and enumerate the controller container.
2. Inspect controller firmware, WMI provider, BIOS/EC versions, sensors, dependencies, and ownership conflicts per resource without writing.
3. Open the Claw recovery journal and snapshot every hardware state the plugin may change.
4. Start WMI OEM events and the motion source.
5. If controller management is enabled, capture the original controller mode, switch to DirectInput only when needed, and await the expected remove/re-enumerate cycle by **physical USB location** (`DEVPKEY_Device_LocationPaths`; container identity is the null GUID on this device and the USB serial exists only in XInput mode).
6. Start the firmware-chord suppressor only after the exact OEM2 logical event source is healthy.
7. Validate and apply the selected semantic hardware profile once, with readback.
8. Publish capability readiness independently as each resource becomes healthy.

WSGM-owned orchestration:

1. Consume the plugin's capability descriptors and canonical physical input.
2. Create the selected HIDMaestro target.
3. Apply only WSGM-owned HidHide entries transactionally using the exact identities reported by the plugin.
4. Register the managed physical source with `UiInputArbiter`; once healthy, WSGM surfaces no longer acquire a Steam Input lease.
5. Map logical OEM1/OEM2 events to the configured allowlisted overlay/QAM actions.

WSGM's desktop UI and overlay remain responsive while these steps run. If the user enters Game Mode before activation finishes, Steam Big Picture is launched or foregrounded immediately and capability readiness continues in the same already-running host. A slow WMI provider, USB mode switch, missing sensor, or failed QAM patch cannot hold either WSGM startup or a mode transition open.

Desktop Mode to Game Mode and Game Mode to Desktop Mode are ordinary session-state notifications. They may change which UI projection is visible and which per-application profile is selected, but they do not recreate the plugin, reopen every device, reset fans/RGB, switch controller mode, or rebuild the virtual controller.

### Deactivation and HC handoff

Deactivation reverses only resources owned by WSGM or the Claw plugin and does not touch another manager.

WSGM first rejects new semantic commands, establishes the SDL/Steam-lease fallback for any open WSGM surface, and neutralizes the virtual target. It keeps its HidHide entries in place while the plugin quiesces and restores the physical controller so the still-captured DirectInput device is not briefly exposed to games or Steam.

The Claw plugin then:

1. Cancels pending hardware work.
2. Unhooks chord suppression and releases only precisely tracked plugin-injected down states, if any; it never injects blanket Win/G/Tab releases.
3. Stops rumble, motion samples, and physical-controller acquisition.
4. Restores the captured controller mode after handles close and awaits re-enumeration.
5. Restores fan tables, custom/full-speed flags, shift/scenario state, and other temporary hardware state from exact snapshots when safe.
6. Closes WMI/HID resources and event subscriptions.
7. Verifies restoration and marks its recovery journal clean.

After the plugin acknowledges that physical acquisition has ended and the original mode/PID is stable, WSGM removes its virtual target and only the HidHide entries it owns, then exits DeviceHost. A bounded-timeout path still honors the user's master toggle, but records the unverified handoff and never claims clean restoration.

Full deactivation occurs only when WSGM exits or the user turns Device Integration off in Settings. Leaving Game Mode is not a deactivation trigger. Runtime plugin removal/update is not allowed while its device cycle is active.

The supported full handoff to Handheld Companion is therefore the Settings master toggle: turning Device Integration off runs the complete cleanup above and then leaves the host stopped. Disabling only WSGM controller management uses the same two-phase controller handoff—neutralize the virtual target while HidHide remains, release and restore the plugin's physical-controller resource, then remove WSGM's virtual target and HidHide state—while the device cycle, power/fan/RGB state services, overlay controls, motion, and plugin-owned OEM event and suppression path remain alive.

If the master toggle is turned off while a WSGM surface is open, the UI input router first attempts to acquire the legacy Steam Input lease and establish its SDL fallback source. The plugin then releases physical acquisition through the two-phase handoff; the router drops the managed canonical source, and WSGM stops DeviceHost. If fallback cannot be established, the surface remains operable by keyboard/touch and shows a warning; it must never keep the device cycle alive contrary to the toggle.

If restoration cannot be verified, the plugin keeps the exact item in its recovery journal and reports an indeterminate result. WSGM presents that state on the next launch. Neither side substitutes a hard-coded "factory" value for a snapshot the plugin failed to read.

### Suspend and resume

On suspend or session lock, cancel pending device calls, stop rumble, quiesce input and IMU publication, unhook/reset firmware-chord state, and close volatile handles. Do not begin a long firmware transaction during the suspend deadline.

On resume/unlock, the plugin rediscovers by container ID, repeats firmware and provider gates, and publishes fresh current-state reads. WSGM reconciles those observations with its persisted desired state and issues any required semantic commands once; the plugin validates and applies each command. No fixed sleep is allowed. Every delay is a cancellable wait for a concrete event, ACK, interface arrival, or bounded retry.

### Conflicting software

Detect and report, but never terminate automatically:

- Handheld Companion.
- MSI Center M and its server/updater/OSD processes.
- MSI Foundation Service presence, which is diagnostic and not by itself proof of active ownership.
- Another WSGM host generation.
- Another process holding the controller/configuration interface.

Ownership is resource-specific: the plugin's physical-controller, WMI power/fan, MCU/RGB, motion, and OEM-suppression resources and WSGM's HidHide/virtual-target resource can each be active or passive independently. External controller management does not automatically disable the plugin-owned OEM event path or WSGM's QAM mapping. Conversely, if HC's Win+G blocker is active, the Claw plugin must not install a second blocker. A hardware resource becomes Passive only after active competing writes, exclusive-access failure, or another demonstrated conflict—not from a process/service name alone. The overlay reports the conflict and points to the Device Integration master toggle in Settings for a complete HC handoff; the plugin never races another application's hardware writes.

## MSI WMI transport

### Provider contract

The reviewed/tested Linux `msi-wmi-platform` patch series documents ACPI WMI GUID `ABBC0F6E-8EA1-11d1-00A0-C90629100000`, instance `0`, fixed 32-byte input/output buffers, and a nonzero returned status byte for success. Treat this low-level contract as Reference until captured through the Windows provider on the target A2VM. Relevant low-level method IDs are:

| Method                     | ID     |
| -------------------------- | ------ |
| Get fan-curve temperatures | `0x0D` |
| Set fan-curve temperatures | `0x0E` |
| Get fan                    | `0x11` |
| Set fan                    | `0x12` |
| Get AP                     | `0x19` |
| Set AP                     | `0x1A` |
| Get data                   | `0x1B` |
| Set data                   | `0x1C` |

The numeric methods above describe the ACPI WMI interface used by Linux. On Windows, HC calls named MOF-provider methods such as `Get_Data`, `Set_Data`, `Get_Fan`, and `Set_Fan` on an enumerated `MSI_ACPI` instance. The Claw plugin validates the provider, instance, board, and interface version rather than hardcoding one instance path.

#### [HW 2026-08-27] Observed provider schema

`MSI_ACPI` is present on the reference unit and exposes 38 methods:

```
GetPackage      SetPackage
Get_EC          Set_EC          Get_EC2
Get_BIOS        Set_BIOS        Get_BIOS_64     Set_BIOS_64
Get_SMBUS       Set_SMBUS       Get_SMBUS_64    Set_SMBUS_64
Get_MasterBattery  Set_MasterBattery
Get_SlaveBattery   Set_SlaveBattery
Get_Temperature Set_Temperature
Get_Thermal     Set_Thermal     Get_Thermal_64  Set_Thermal_64
Get_Fan         Set_Fan
Get_Device      Set_Device
Get_Power       Set_Power
Get_Debug       Set_Debug
Get_AP          Set_AP
Get_Data        Set_Data
Get_WMI
Get_PE          Set_PE
```

Every method this plan depends on — `Get_Data`/`Set_Data`, `Get_AP`/`Set_AP`, `Get_Fan`/`Set_Fan`,
`Get_Temperature` — is present.

**The 32-byte buffer contract is confirmed on Windows.** Each of those methods takes a single in/out
parameter `Data`, an `EmbeddedInstance` of:

```
class Package_32 {
  UInt8 Bytes[];
}
```

`GetPackage` uses the differently shaped class `Package`. The Windows named-method provider and the
Linux ACPI WMI interface therefore share the same 32-byte package. The `msi-wmi-platform` buffer
*shape* can be promoted from Reference to consistent-with-hardware; no field *layout* is confirmed
by this alone.

Class properties are only `Active [Boolean]` and `InstanceName [String]`. There is no readable state
surface — every read is a method invocation, so instance enumeration is cheap and does not evaluate
a control method.

Sibling classes present: `MSI_Event`, `MSI_AP`, `MSI_Device`, `MSI_System`, `MSI_Software`,
`MSI_CPU`, `MSI_VGA`, `MSI_Power`, `MSI_Master_Battery`, `MSI_Slave_Battery`. Of these only
`MSI_Event` returns instances; the rest report `WBEM_E_NOT_SUPPORTED`, meaning no instance provider
rather than blocked access.

**`Get_EC`/`Set_EC`/`Get_EC2` and `Get_SMBUS` are reachable through this provider.** The conclusion
that PawnIO and direct EC access are unnecessary holds, but for a different reason than assumed —
EC is not out of reach, it is reachable through a path WSGM deliberately declines to generalize.
State the no-generic-EC-service rule as restraint, not as a platform limitation.

**[HW 2026-08-27] Three ACPI WMI nodes exist, not one:**

```
ACPI\PNP0C14\0
ACPI\PNP0C14\DSARDEV
ACPI\PNP0C14\TESTDEV
```

Only one of them exposes `MSI_ACPI`. The elevated probe returns exactly **one** instance:

```
InstanceName = 'ACPI\PNP0C14\0_0'   Active = True
```

**HC's `ACPI\PNP0C14\0_0` is exactly right.** The apparent mismatch was a category error: `0_0` is
the WMI `InstanceName`, while `ACPI\PNP0C14\0` is the PnP instance ID of the same node. `DSARDEV`
and `TESTDEV` are unrelated WMI-ACPI providers that expose no `MSI_ACPI`.

Discovery is still the correct implementation — the instance must be validated against board and
provider version rather than assumed — but this is a robustness measure, not a correction of HC.

#### [HW 2026-08-27] First register capture

Elevated, read-only, `Get_*` methods only. Machine idle on AC, Sport scenario, custom fan control
flag clear.

> **Mixed provenance — see the stock capture that follows.** This first capture was taken with
> Handheld Companion having run on the machine. A later clean post-reboot capture established which
> of these values are factory and which were HC's. The fan curve here is HC's; the power limits turn
> out to be factory.

**Byte 0 of every response is status; `0x01` means success.** This confirms the "nonzero returned
status byte" convention from the Linux series on the Windows provider.

`Get_Data` (input byte 0 = address):

| Address | Raw | Decoded | Note |
| --- | --- | --- | --- |
| `0x50` SPL/PL1 | `01 1E …` | **30 W** | **Factory value** — unchanged in the clean post-reboot capture. `ClawA2VM.Open()` happens to write the same number |
| `0x51` SPPT/PL2 | `01 25 …` | **37 W** | **Factory value**, likewise unchanged. Matches the stated 37 W ceiling |
| `0x52` FPPT/PL3 | `01 00 …` | 0 | Not exposed, consistent with "do not write on A2VM" |
| `0xD2` scenario | `01 C4 …` | **`0xC4` Sport** | Confirms the scenario address and the Sport value |
| `0xD4` | `01 00 …` | 0 | HC's custom-fan-enable path; currently off |
| `0xD7` charge limit | `01 50 …` | **80 %** | Confirms the optional charge-threshold capability |

Every documented address returned a plausible in-range value on the first read. The address map in
this plan is corroborated on `MS-1T52`.

`Get_AP` / `Get_Fan` / `Get_Temperature` (input byte 0 = subfeature):

```
Get_AP          sub0 : 01 00 00 C4 80 50 00 …
Get_Fan         sub0 : 01 00 C7 00 CF 00 …
Get_Temperature sub0 : 01 27 00 …

Get_AP          sub1 : 01 00 00 03 00 …
Get_Fan         sub1 : 01 1E 1E 1E 25 2D 69 69 96 00 …
Get_Temperature sub1 : 01 00 58 64 32 3C 46 50 58 00 …

Get_AP          sub2 : 01 00 01 00 00 08 00 …
Get_Fan         sub2 : 01 1E 1E 1E 25 2D 69 69 96 00 …
Get_Temperature sub2 : 01 00 58 00 32 3C 46 50 58 00 …
```

**Fan tachometer confirmed — two channels, big-endian divisor.** `Get_Fan` sub0 carries two
16-bit big-endian values, `0x00C7` and `0x00CF`. Applying the documented `RPM = 480000 / value`
gives **2412 RPM and 2319 RPM** — both entirely plausible idle fan speeds for this chassis. Channel
count, byte order, and the conversion formula are all corroborated. Physical left/right assignment
is still unproven and the plan's Left fan / Right fan labelling should stand until it is.

**The six-versus-eight fan-table discrepancy resolves in favour of six.** `Get_Temperature` sub1
places its curve points at **bytes 1 and 4–8**, exactly as the Linux series describes:

```
byte:   1     4     5     6     7     8
val:  0x00  0x32  0x3C  0x46  0x50  0x58
°C:      0    50    60    70    80    88
```

Those are precisely the six factory-curve temperatures already documented in this plan
(0/50/60/70/80/88 °C). HC's eight-value table and its 11-to-8 mapping do not describe this
firmware's buffer. **Implement six points at bytes 1 and 4–8.**

Bytes 2–3 hold `0x58 0x64` (88, 100) on sub1 and `0x58 0x00` on sub2 — outside the curve, likely
throttle/critical thresholds. Unidentified; do not write.

**Fan duty scaling — resolved by the clean stock capture, see below.** In this HC-influenced capture
`Get_Fan` sub1 and sub2 both returned `1E 1E 1E 25 2D 69 69 96` = `30 30 30 37 45 105 105 150`. The
values above 100 are not a scale to decode: they are HC's ×1.5 overdrive of a field that is already
direct percent. The stock capture returns `40 0 40 49 58 67 75 75`, giving the documented factory
curve from bytes 2–7.

Do not guess this. It needs the MSI Center M before/after comparison this plan already specifies —
read both channel buffers, change one curve point in the OEM utility, read again. Until then no fan
write is implementable, and the duty entries' byte positions are also unconfirmed (bytes 1–8 are
populated, but which six are the curve is not established the way the temperature side now is).

#### [HW 2026-08-27] Clean stock capture, post-reboot

Taken after a reboot with Handheld Companion, MSI Center M and ClawTweaks all confirmed not running
and `MSI Foundation Service` stopped. This is the authoritative factory reference.

| Signal | Stock value | Notes |
| --- | --- | --- |
| PL1 `0x50` | **30 W** | Factory; identical to the HC-influenced capture |
| PL2 `0x51` | **37 W** | Factory; the hardware ceiling |
| PL3 `0x52` | 0 | Not exposed |
| Scenario `0xD2` | **`0xC1`** | supported + active + mode 1 = **Green**. HC-influenced capture read `0xC4` (Sport), i.e. a user/app selection |
| Fan custom `0xD4` | `0x00` | Off |
| Fan full-speed `0x98` | `0x02` | Bit 7 clear; `0x02` is the stock low-byte value |
| Charge limit `0xD7` | **80 %** | **Factory default**, not an HC setting |
| Fan RPM sub0 | `195`, `196` → 2462 / 2449 RPM | Idle |
| Fan duty sub1/sub2 | `0/40/49/58/67/75 %` | Exactly the documented factory curve |
| Temp points sub1/sub2 | `0/50/60/70/80/88 °C` | Unchanged from the first capture |
| `Get_AP` sub1 byte 3 | `0x05` | Was `0x03` when HC had run — tracks something, unidentified |
| `Get_AP` sub2 byte 5 | `0x14` | Was `0x08` when HC had run — unidentified |

**Live temperature is confirmed.** `Get_Temperature` sub0 read `0x27` (39 °C) on the warm idle
machine and `0x3F` (63 °C) shortly after boot. It varies with thermal state, so it is a live sensor,
not a curve point. This plan's earlier statement that live temperatures require a separately
validated provider is wrong — the Overview section can source temperature from the provider already
in use. Confirm the unit is °C across a wider range before displaying it.

**Scenario encoding resolved.** `GetShiftModeValue` maps Comfort→0, Green→1, Eco→2, User→3,
**Sport→4**, so this plan's `0xC0`–`0xC4` table is correct and HC's `ShiftType` enum ordinals are
merely internal names that do *not* match the wire values. Combined with bit 7 = supported and
bit 6 = active, `0xC1` decodes as an active Green scenario.

Minor HC defect for the record: `SetShiftMode` starts with `ShiftModeValueInEC &= 195` (`0xC3`),
which clears bit 2 and therefore destroys Sport (mode 4) on the `Active`/`Deactive` paths. Only the
`ChangeToCurrentShiftType` path re-adds the mode value afterwards.

**Provider interface version is 8.0.** `Get_WMI` returned `01 02 08 00 …`. HC reads this as
`major = data[1]`, `minor = data[2]` after stripping status — giving **major 8, minor 0** here. HC
derives `isNew_EC => WmiMajorVersion > 1`, so this unit is firmly in its "new EC" branch. The leading
`0x02` payload byte is unidentified. This version number is a good capability gate and belongs in the
identity record alongside BIOS and EC versions.

**Possible live temperature source.** `Get_Temperature` sub0 returns `0x27` = 39, which reads like a
live °C value rather than a curve point. This plan currently states that `Get_Temperature` returns
curve points only and that live temperatures need a separately validated provider. That may be
wrong. Worth a follow-up: sample sub0 under load and see whether it tracks. If it does, the Overview
section gets live temperatures without a second source.

**`Get_AP` observations.** sub1 byte 1 is `0x00`, so the Linux custom-curve enable bit (byte 1
bit 7) is clear — consistent with `Get_Data 0xD4 = 0` and with the machine running stock fan
control. The two independent indicators agree, which is a useful cross-check for the conflicting
enable paths recorded in the fan section. sub0 additionally mirrors scenario (`0xC4`) and charge
limit (`0x50`), so it appears to be a summary block.

The ACPI method is treated as non-thread-safe. One FIFO owns every MSI WMI transaction, including reads. Each call has a short bounded timeout, checks returned length and status, and records the operation name—not sensitive raw memory—in diagnostics.

Handheld Companion can install `msiapcfg.dll`, change `MofImagePath`, and restart an ACPI device to create its `MSI_ACPI` class. WSGM must not copy that runtime behavior.

**[HW 2026-08-27] Provider provenance on the reference unit.**

```
HKLM\SYSTEM\CurrentControlSet\Services\WmiAcpi
  MofImagePath = C:\WINDOWS\SysWOW64\msiapcfg.dll
  ImagePath    = \SystemRoot\System32\drivers\wmiacpi.sys   (stock Microsoft driver)
```

`msiapcfg.dll` is **genuinely MSI's**: Authenticode status `Valid`, signer
`CN="Micro-Star International CO., LTD."`. Its file description is *"Resource only DLL containing MOF
for ASL code"* — it carries no executable logic, only the MOF schema mapping WMI classes onto ACPI
methods. A second identical-timestamp copy lives at
`C:\Program Files (x86)\MSI\MSI Center M\msiapcfg.dll`, and MSI Center M is installed here.

**The real prerequisite is the Intel chipset drivers, not MSI Center M.** Confirmed by the device
owner: HC operates correctly with MSI Center M completely uninstalled; only the Intel chipset drivers
are required. That is consistent with the mechanism — the chipset drivers are what make the ACPI
devices enumerate, `wmiacpi.sys` (stock Microsoft) then binds `ACPI\PNP0C14`, and the `MofImagePath`
resource DLL supplies the `MSI_ACPI` MOF schema on top. The `MSI Center M` copy of the DLL on this
unit is incidental, not the source of the capability.

So the plugin's declared dependency is an ordinary chipset-driver prerequisite, present in any
supported configuration. WSGM never redistributes anything: the plugin *detects* the provider and
reports WMI-backed capabilities unavailable when it is missing. `P0-040` is not a
redistribution-rights gate.

One detail still worth pinning down for the dependency-health check: whether `msiapcfg.dll` and its
`MofImagePath` registration are themselves part of the driver/OEM image, or whether HC's deploy path
is what puts them there on a machine that has never run MSI Center M. It changes the wording of the
prerequisite message, not the design.

Note for the dependency-health check: HC additionally ships its own copy of `msiapcfg.dll` and can
deploy it, rewrite `MofImagePath`, and restart `ACPI\PNP0C14` via `CheckAndDeployWmiAcpi()`,
`CheckAndFixRegistry()` and `RestartAcpiPnpDevice()`. WSGM does none of that at runtime. But it means
a machine where HC has run may have a provider registration HC created, so a WSGM health check should
report *what it found* rather than asserting the OEM put it there.

If the provider is nonetheless absent or damaged, the Claw plugin reports its WMI-backed
capabilities unavailable and WSGM presents that state. Neither side modifies the registry or
restarts ACPI during normal WSGM runtime.

## TDP and power limits

### A2VM limits

Use the A2VM-specific `MS-1T52` constraints:

| Limit    | WMI data address | Safe range  | Purpose                                           |
| -------- | ---------------- | ----------- | ------------------------------------------------- |
| SPL/PL1  | `0x50`           | 8–30 W      | Sustained package power and the normal TDP slider |
| SPPT/PL2 | `0x51`           | 8–37 W      | Short boost ceiling                               |
| FPPT/PL3 | `0x52`           | Not exposed | Do not write on A2VM                              |

The payload is address byte zero followed by a little-endian 32-bit integer watt value. HC writes only the low byte because current values are below 256; the Claw power capability encodes and validates the complete field.

### UI behavior

The native Steam QAM and the primary overlay TDP slider control PL1 from 8 to 30 W in 1 W steps. The overlay's advanced power section exposes PL2 with an explanation of short boost. The Claw power capability authoritatively enforces:

- `8 <= PL1 <= 30`.
- `8 <= PL2 <= 37`.
- `PL2 >= PL1`.
- No fractional values unless future firmware proves support.

Planned WSGM presets, to be confirmed on the user's unit, are:

| Preset              |  PL1 |  PL2 |
| ------------------- | ---: | ---: |
| Battery             |  8 W |  9 W |
| Balanced            | 17 W | 18 W |
| Performance         | 30 W | 31 W |
| Performance + boost | 30 W | 37 W |

These are WSGM-owned desired profiles informed by HC's current A2VM values; they are not claimed as immutable MSI factory profiles. The Claw plugin validates, orders, applies, reads back, and rolls back the resulting hardware operations.

### Transaction

1. Validate ownership, AC/battery policy, board, range, and PL1/PL2 relationship.
2. Read current PL1 and PL2.
3. Write PL2 first when raising PL1 beyond it; otherwise write PL1 first when lowering both.
4. Read both back.
5. Publish the applied state only after the readback matches.
6. On a partial failure, roll back to the captured pair and report degraded power control.

Do not reproduce HC's transient startup writes or poll-and-rewrite loops. Reapply only on profile change, power-source policy change, explicit command, or validated resume recovery.

### MSI shift/scenario mode

The MSI scenario byte is a separate firmware policy writer at data address `0xD2`. Known values include Sport `0xC4`, Comfort `0xC0`, Green `0xC1`, Eco `0xC2`, and User `0xC3`. A2VM does not use the later gen4 Manual value.

Shift/scenario mode can impose its own power ceilings, so it cannot be ignored. M0 maps every scenario's PL1/PL2 interaction on AC and battery. M2 either changes scenario plus limits as one rollback-capable profile transaction, or rejects a requested pair that the current scenario cannot honor. The original scenario is snapshotted and restored on handoff. A standalone scenario selector ships only after those interactions are understood.

## Fan control

### Model

The A2VM has two physical fan channels. MSI's protocol names them CPU/GPU, but Claw hardware maps them more safely as left/right; the UI uses Left fan and Right fan until a physical thermal-domain mapping is proven. The plugin exposes three modes:

- Automatic: firmware/OEM curve ownership.
- Custom: firmware executes a WSGM-supplied curve.
- Full speed: explicit temporary override with a prominent active indicator.

`ClawA2VmFanCapability` does not implement a high-frequency software PWM loop. It edits the firmware's curve and lets firmware enforce it.

Independent A2VM evidence supports this observed/reference six-point factory curve on multiple units; it is not assumed immutable on the user's firmware:

| Point | Temperature | Factory duty shown by MSI-style UI |
| ----- | ----------: | ---------------------------------: |
| 1     |        0 °C |                                 0% |
| 2     |       50 °C |                                40% |
| 3     |       60 °C |                                49% |
| 4     |       70 °C |                                58% |
| 5     |       80 °C |                                67% |
| 6     |       88 °C |                                75% |

HC currently writes an eight-value table through an inconsistent 11-to-8 mapping and restores a contradictory hard-coded default. WSGM must not copy that abstraction. The bring-up tool reads the full left/right channel buffers before and after one MSI Center M curve edit to establish the exact `MS-1T52` byte layout and unit conversion.

The fan duty and curve-temperature operations are separate WMI subfeatures. Channel subfeatures are `1` and `2`; fan subfeature `0` is the RPM query. The transport follows a read-modify-write pattern on each full 32-byte buffer.

**[HW 2026-08-27] Confirmed on the reference unit.** Subfeature layout is as described: `0` returns
tachometer data, `1` and `2` are the two channels. Byte 0 is status (`0x01` = success).

The **temperature** side is settled: six curve points at **bytes 1 and 4–8**, matching the Linux
layout and yielding exactly the documented 0/50/60/70/80/88 °C factory points. HC's eight-value
table does not describe this firmware. Implement six points at bytes 1 and 4–8.

The **duty** side is now settled too, from reading HC's implementation at the audited revision.

**Duty is six entries at bytes 2–7, in direct percent.** Settled by the stock capture below, which
supersedes an earlier reading of this buffer that inferred a `0–150` scale from HC's write path.

A clean post-reboot capture with HC never having run returns:

```
Get_Fan sub1 : 01 | 28 00 28 31 3A 43 4B 4B     = 40, 0, 40, 49, 58, 67, 75, 75
Get_Fan sub2 : 01 | 00 00 28 31 3A 43 4B 4B     =  0, 0, 40, 49, 58, 67, 75, 75
```

Reading bytes 2–7 against the temperature points at bytes 1 and 4–8:

| Temp °C | 0 | 50 | 60 | 70 | 80 | 88 |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Duty % | 0 | 40 | 49 | 58 | 67 | 75 |

That is **exactly** the factory curve already documented in this plan. The raw bytes *are* the
percentages — no scaling. **The Linux series' "six duty entries at bytes 2–7" is correct**, and the
duty and temperature tables use different offsets within the same buffer, which is unusual but
consistent across both channels.

Bytes 1 and 8 sit outside the curve: byte 8 repeats the final duty (75), and byte 1 differs per
channel (40 on sub1, 0 on sub2). Unidentified — preserve them, do not write them.

**Consequence: HC's software fan mode is the broken path, not its restore path.** HC writes
`percent / 100.0 * 150.0`, which overdrives a field that is already direct percent — a requested
67 % is written as 100, and 100 % as 150. Its hardcoded restore table
`{40, 0, 40, 49, 58, 67, 75, 75}` is, by contrast, byte-for-byte the true factory buffer. An earlier
note in this document had this exactly backwards.

The pre-reboot capture (`30 30 30 37 45 105 105 150`) is HC's scaled output, consistent with a user
curve of roughly 20/20/25/30/70/70/100 % run through the ×1.5 overdrive. Values above 100 in a
percent field are the signature of that bug.

### Safety rules

- Snapshot left and right channel tables independently.
- Do not enable Custom or Full speed if the prerequisite snapshot failed.
- Validate monotonic temperatures and duties.
- Once raw scaling is proven, permit the hardware's validated 0–100% duty range; 75% is the observed factory duty at 88 °C, not a maximum.
- Preserve at least the captured factory duty at the hottest points in the first release, while leaving an explicit 100% Full speed path for emergency cooling.
- Apply both channel transactions under one logical operation and roll back both if either fails.
- Verify the custom-enable/full-speed bits and table readback.
- Restore exact captured tables and flags on deactivation or HC handoff.
- Stop presenting applied state after any WMI timeout or status failure.

The reviewed Linux path read-modify-writes `Set_AP` subfeature `1`, byte 1 bit 7 to enable custom curves. HC instead reads `Get_AP` subfeature `1` and writes the resulting bit through `Set_Data` address `0xD4`; these are conflicting paths, not interchangeable instructions. M0 captures each target operation and M2 enables only the path verified on the user's Windows provider.

### [HW 2026-08-27] Transport facts confirmed against HC and this unit

Read from HC at the audited revision and cross-checked against the register capture. These are
protocol facts about the hardware; the implementation is ours.

| Operation | Path | Corroboration on this unit |
| --- | --- | --- |
| Status byte | `outBytes[0]`, success when `== 1` | Every read returned `0x01` |
| Payload | Data begins at byte 1; the status byte is stripped | `Get_AP` declared lengths 6/3/7 for sub 0/1/2 match the capture exactly |
| PL1 / PL2 / PL3 | `Set_Data` index `0x50` / `0x51` / `0x52`, value in byte 1 | Reads returned 30 W / 37 W / 0 |
| Fan curve duty | `Set_Fan` sub 1 (CPU) and 2 (GPU), 8 bytes at offset 1 | Capture has 8 populated bytes at 1–8 |
| Fan curve temps | `Get_Temperature`, six points at bytes 1 and 4–8 | `0/50/60/70/80/88 °C` |
| Fan custom enable | Read `Get_AP` sub 1, set **bit 7** of data[0], write `Set_Data` `0xD4` | `0xD4` reads 0; `Get_AP` sub1 byte 1 is `0x00` — two independent indicators agree |
| Fan full speed | Read `Get_Data` `0x98`, set **bit 7**, write back | Not yet read |
| Scenario / shift | Read `Get_AP` sub 0 data[2]; write `Set_Data` `0xD2` | data[2] = our raw byte 3 = `0xC4` |

**The scenario byte is a bitfield, not a flat enum.** HC decodes it as bit 7 = supported, bit 6 =
active, low 6 bits = mode. `0xC4` is therefore *supported + active + mode 4*, not an opaque "Sport"
constant. That also explains the documented value set: Comfort `0xC0`, Green `0xC1`, Eco `0xC2`,
User `0xC3`, Sport `0xC4` are modes 0–4 with both flags set. Implement the bitfield; do not copy the
enum table.

### [HW 2026-08-27] Other confirmed reads

| Address | Value | Meaning |
| --- | --- | --- |
| `0x98` | `0x02` | Fan full-speed flag. Bit 7 clear = off, consistent with HC's read-set-bit-7-write path. Low bits `0x02` unidentified — do not write |
| `0x00` | `0x00` | Unidentified |
| `0x01` | `0x80` | Unidentified, bit 7 set |

**OverBoost is A1M-only and does not exist here.** HC exposes an "OverBoost" toggle driven by the
UEFI variable `MsiDCVarData` (`{DD96BAAF-145E-4F56-B1CF-193256298E99}`), gated in the UI on
`box[1] != 0`, which `Open()` initialises only when the variable reads back. It is an A1M-era
feature; A2VM has no such variable, so the toggle never appears. WSGM excludes UEFI variable writes
by design — this is now a *known-existing capability we decline*, not an unknown. (A direct read
attempt was inconclusive: `SeSystemEnvironmentPrivilege` could not be enabled, `ERROR_PRIVILEGE_NOT_HELD`.)

Relevant side effect worth knowing: `IntelProcessor` uses `GetOverBoost()` to decide whether to
force the OEM power path. With no UEFI variable, that returns false on A2VM.

### HC defects not to reproduce

Reading the source turned up these things worth not inheriting:

1. **`SetFanTable` reads and discards.** It calls `Get_Fan` into `data`, never uses it, then builds a
   fresh 32-byte package containing only the 8 duty bytes. It is a blind write, not the
   read-modify-write it appears to be, and it **zeroes bytes 9–31** of the firmware buffer. Our
   implementation must genuinely preserve the untouched remainder.
2. **Software fan mode overdrives the duty field by 1.5×.** It writes `percent / 100.0 * 150.0` into
   a field that the stock capture proves is direct percent, so a requested 67 % is written as 100 and
   100 % as 150. Its hardcoded restore table `{40, 0, 40, 49, 58, 67, 75, 75}` is, by contrast,
   byte-for-byte correct. The inconsistency this plan suspected is real — but it is the *software
   mode* that is wrong, not the restore path.
3. **`SetFanControl` ignores its own failure flag.** It uses `data[0]` regardless of `readSuccess`.
   Our rule that no write happens if its prerequisite read failed directly addresses this.
4. **`Open()` rewrites M1/M2 profile memory and syncs to ROM on every single launch.** Lines 332–343:
   two 64-byte `GetM12` writes, `SyncToROM()`, then `SwitchMode()`, separated by
   `Thread.Sleep(300/500/500/500/2000)`. This is the EEPROM-wear pattern this plan already refuses,
   confirmed in source — and it runs unconditionally, not only when M1/M2 are actually missing.
5. **Transient startup power writes.** `ClawA1M.Open()` sets `35/35`, then `ClawA2VM.Open()`
   immediately overwrites with `30/37`. Two writes where zero are needed, and the reason the reference
   unit reads 30/37 at rest.
6. **Sleep-based sequencing throughout.** Fixed `Thread.Sleep` values stand in for waiting on ACKs,
   PnP events, or deadlines everywhere in the open/mode-switch path.
7. **Nearest-firmware address selection.** `FirmwareDevice => deviceVersions.MinBy(v => Math.Abs(v.Firmware - Firmware))`
   picks the numerically closest known firmware. `IsSupported` requires an exact match and would log
   this unit as **Unsupported** at `0x229` — but `GetRGB` and `GetM12` read addresses from
   `FirmwareDevice` regardless of `IsSupported`. So on this device HC writes RGB and M1/M2 profile
   data using **`0x219`'s addresses on `0x229` firmware**. This is precisely the "no firmware address
   is selected by numerical proximity" rule, and here is the live counterexample.

HC's firmware table is also wider than the three rows recorded in the RGB section above —
`0x163, 0x166, 0x167, 0x211, 0x217, 0x219, 0x308, 0x411`, each carrying an RGB base plus M1/M2
addresses for **both** DInput and XInput. None of them is `0x229`.

### Telemetry

Expose both fan RPM values, current mode, and the last verified curve.

**[HW 2026-08-27] The tachometer contract is confirmed.** `Get_Fan` subfeature `0` returns two
16-bit big-endian divisors. On the idle reference unit: `0x00C7` and `0x00CF`, which under
`RPM = 480000 / value` give **2412 and 2319 RPM** — plausible idle speeds. Big-endian byte order,
two channels, and the conversion formula all hold. Zero still means stopped.

**[HW 2026-08-27] Channel-to-side is now established.** A per-channel duty test drove one channel to
90 % while holding the other at 0 %, with the operator identifying the moving side:

| WMI subfeature | Physical side |
| --- | --- |
| **1** | **Left fan** |
| **2** | **Right fan** |

So this plan's Left fan / Right fan labelling is correct and now mapped. MSI's protocol names these
CPU and GPU respectively (HC's comment: `iDataBlockIndex = 1 // CPU`, `2 // GPU`); **do not surface
that naming**, since it describes an assumed thermal domain rather than a verified one, while the
physical side is measured fact.

#### [HW 2026-08-27] The fans are physically slow to spin up and down

Confirmed by the device owner as a genuine hardware property, not a readback artefact. Observed
across the load and per-channel runs:

| Transition | Observed |
| --- | --- |
| 0 → 90 % duty | ~2 700–2 900 rpm after ~6 s |
| idle → sustained load | 2 759 rpm at +5 s, 3 582 at +15 s, 4 174 at +25 s |
| 90 % → 0 % duty | still 3 453 rpm six seconds after the command |

Design consequences, all of which bite if assumed away:

- **Never verify a fan command against the tachometer within a short window.** Verify the commanded
  table by reading it back instead. RPM converges minutes later, not seconds. This plan's general
  rule that a command is `AppliedVerified` only with readback evidence must, for fans, mean *table*
  readback — treating RPM as the evidence would produce false `Rejected` results constantly.
- **Full-speed override has a real latency floor.** It is not an instant emergency stop-gap; the UI
  must not imply the fans jump immediately, and any thermal-emergency reasoning has to account for
  seconds of ramp.
- **Curve edits feel unresponsive.** A user dragging a curve point and watching the RPM readout will
  see nothing for several seconds. The overlay should show the commanded value immediately and treat
  measured RPM as a separate, lagging telemetry field rather than confirmation of the edit.
- **Acceptance tests need generous timeouts.** A "fan responded to the new curve" check with a
  five-second budget will be flaky. Gate on the table, or allow tens of seconds.
- The `fanside` bring-up phase works only because the *operator* observes airflow directly; an
  automated version keyed on RPM would need a much longer settle time per channel.

**[HW 2026-08-27] Live temperature confirmed — `Get_Temperature` subfeature `0` is a real sensor.**
Sampled across an idle → all-core load → cooldown cycle:

| Phase | Temperature | Fan A | Fan B |
| --- | ---: | ---: | ---: |
| idle (avg of 4) | **50.8 °C** | 0 rpm | 0 rpm |
| load +5 s | 71 °C | 2759 | 2727 |
| load +15 s | 79 °C | 3582 | 3556 |
| load +25 s | **82 °C** | 4174 | 4138 |
| cooldown +16 s | 52 °C | 2652 | 2652 |

A 31 °C excursion that tracks load and recovers on cooldown. Earlier revisions of this plan asserted
that `Get_Temperature` returns only curve points and that live temperature requires a separate
validated provider — **that is wrong**. The Overview section can source live temperature from the
MSI provider already in use, and no second telemetry source is needed.

Two further confirmations from the same run:

- **Fans genuinely report 0 rpm at idle** — the divisor reads zero, matching the documented
  "zero means stopped" convention, and this device does run passively when cool.
- Both channels track within ~1 % of each other throughout, consistent with the identical stock
  curve read from both channel buffers.

## MCU/vendor HID transport

The controller configuration protocol uses 64-byte reports. Confirmed framing and commands are:

| Item                   | Value           |
| ---------------------- | --------------- |
| Output prefix          | `0F 00 00 3C`   |
| Output report ID       | `0x0F`          |
| Input report ID        | `0x10`          |
| Read profile / ACK     | `0x04` / `0x05` |
| Generic ACK            | `0x06`          |
| Write profile          | `0x21`          |
| Sync profile to ROM    | `0x22`          |
| Switch controller mode | `0x24`          |
| Read mode / ACK        | `0x26` / `0x27` |
| Reset                  | `0x28`          |

### [HW 2026-08-27] Endpoint capabilities

Probed with `HidP_GetCaps` on the live MCU interface (`HID\VID_0DB0&PID_1901&MI_01`):

| Property | Value |
| --- | --- |
| Usage page / usage | `0xFFA0` / `0x0001` |
| Input report length | **64** bytes (includes report ID) |
| Output report length | **64** bytes (includes report ID) |
| **Feature report length** | **0** |
| `bcdDevice` | `0x0229` |
| Product string | `Xbox360 Controller for Windows` |

The 64-byte report size is confirmed at the HID layer, and since the length includes the report ID,
the usable payload is 63 bytes after the leading `0x0F`.

**The full topology differs substantially between modes.** Both were captured live:

| | XInput (`0x1901`) | DirectInput (`0x1902`) |
| --- | --- | --- |
| Gamepad | `IG_00` — **raw HID access denied** | `MI_00&COL01` — usage `0x0001`/`0x0005`, in 64 / out 32 / **feature 48**, 9 value caps |
| MCU | `MI_01` — `0xFFA0`/`0x0001`, in 64 / out 64 / **feature 0** | `MI_00&COL02` — `0xFFF0`/`0x0040`, in 64 / out 64 / **feature 64** |
| Keyboard | `MI_02&COL01`, in 9 | `MI_01&COL01`, in 9 |
| Mouse | `MI_02&COL02`, in 8 | `MI_01&COL02`, in 8 |
| Consumer | `MI_02&COL03`, in 5 | `MI_01&COL03`, in 5 |
| Product string | `Xbox360 Controller for Windows` | garbled/invalid |

Three consequences:

1. **Feature reports exist only in DirectInput mode.** The MCU endpoint reports `feature = 0` under
   XInput but `feature = 64` under DirectInput, and the DirectInput gamepad collection additionally
   exposes 48-byte feature reports. An earlier note here stated flatly that this endpoint has no
   feature reports — that is true only of XInput mode and must not be generalised.
2. **In XInput mode the gamepad cannot be read over raw HID at all.** Opening
   `HID\VID_0DB0&PID_1901&IG_00` fails with `ERROR_ACCESS_DENIED` (5); Windows reserves
   XInput-exposed interfaces. Under DirectInput the pad is an ordinary readable HID game-pad
   collection. This is an independent reason to prefer DirectInput for managed acquisition, on top of
   M1/M2 exposure.
3. **The product string is not an identity signal.** It reads correctly under XInput and returns
   garbage under DirectInput, so identity must come from VID/PID/`bcdDevice` and location, never from
   the string.

The DirectInput gamepad's 32-byte output report is the rumble path; HC writes an 11-byte payload
beginning with report ID `0x05` into it.

Interface indices shift by one between modes (`MI_02` → `MI_01` for the keyboard/mouse/consumer
group), which is a second reason index-based binding is unsafe.

### [HW 2026-08-27] ReadProfile works — profile memory is directly readable

This is the finding that closes the firmware `0x0229` gap. `ReadProfile` (`0x04`) accepts an
arbitrary address and length and returns the contents, so the addresses this plan could not obtain
from any published table can simply be **read from the device**.

```
request : 0F 00 00 3C 04 01 <addrHi> <addrLo> <len>
response: 10 00 00 3C 05 01 <addrHi> <addrLo> <len> <len bytes of data>
```

Confirmed live. Input report ID is **`0x10`** as this plan states, and the response preamble is
`10 00 00 3C` followed by `05` (`ReadProfileAck`) and the profile index. Note the echoed address in
the ack is not a straightforward big-endian pair — decode it defensively or ignore it and track
requests yourself.

`ReadGamepadMode` (`0x26`) also works and returned `10 00 00 3C 27 01`, i.e. mode `01` = XInput,
independently confirming both the ack shape and the mode encoding. `ReadCurrentProfile` (`0x0B`) and
`ReadRGBStatus` (`0x0D`) returned nothing in this form — they need different arguments or are not
supported on this firmware.

#### Profile 1 memory map on firmware `0x0229`

Dumped `0x0000`–`0x02DF` in 32-byte reads:

| Region | Contents |
| --- | --- |
| `0x0000`–`0x0019` | zero |
| `0x0020`–`0x0039` | header block, `01 00 32 32 … 62 01 00 04 00 19 32 FF FF …` |
| `0x003A`–`0x0099` | **button map** — twelve 8-byte entries, stride 8, pattern `01 00 <flags> <NN> 00 00 00 00` with `NN` running `01`…`0C` |
| `0x009A`–`0x00B9` | trailing `0C 00 00 00 00 00` patterns |
| **`0x00BA`** | **M1 entry** — `01 00 00 7A FF FF FF FF` |
| **`0x0163`** | **M2 entry** — `01 00 00 7D FF FF FF FF` |
| `0x01C0`–`0x01F9` | per-frame headers, `01 02 0C 00 64 13`, `01 02 0C 00 64 14`, `01 01 05 …`, `01 01 0A …` |
| **`0x01FA`** | **RGB profile header** — `00 04 09 03 64` = index 0, **4 frames**, effect `0x09`, speed `0x03`, **brightness 100**, followed by colour triplets |
| `0x024A` | a second, **zeroed** RGB header — `00 01 09 03 00`, brightness 0, all-zero payload |
| `0x0260`+ | zero |

#### [HW 2026-08-27] RESOLVED ON HARDWARE: base is `0x024A`, zone order mapped

A bring-up run wrote test colours to `0x024A` and the LEDs responded. **`0x024A` is the correct base
on firmware `0x0229`.** HC's table and HHD's version rule are both right; the speculation below about
`0x01FA` is wrong and retained only as a record of the reasoning.

Physical zone order, one zone lit white at a time and reported by the operator:

| Index | Physical location |
| ---: | --- |
| 0 | Right ring — bottom left |
| 1 | Right ring — bottom right |
| 2 | Right ring — top right |
| 3 | Right ring — top left |
| 4 | Left ring — top right |
| 5 | Left ring — top left |
| 6 | Left ring — bottom left |
| 7 | Left ring — bottom right |
| 8 | ABXY button group |

**HC's grouping claim is confirmed**: right ring = 0–3, left ring = 4–7, buttons = 8. Both rings run
counter-clockwise but start from diagonally opposite corners — right begins bottom-left, left begins
top-right.

That asymmetry is recorded for the encoder's benefit only. **WSGM scopes lighting to three logical
zones — Right Ring, Left Ring, Buttons — and does not expose per-LED control**, so the start-corner
difference never reaches the UI. See the locked scope in the capability model below.

#### Superseded: which RGB block is live (resolved above)

Two RGB-shaped headers exist in profile 1:

| Address | Contents | Reading |
| --- | --- | --- |
| `0x01FA` | `00 04 09 03 64` + colour triplets | index 0, 4 frames, effect `0x09`, speed `0x03`, **brightness 100** |
| `0x024A` | `00 01 09 03 00` + all zeros | index 0, 1 frame, effect `0x09`, speed `0x03`, **brightness 0** |

An earlier revision of this section concluded from "populated versus zeroed" that `0x01FA` must be
the live base and that this firmware therefore mixes the old and new layouts. **That inference was
wrong and is retracted.** Both blocks are simply RGB structures; a zeroed one is exactly what a
"lights off" state looks like, and cannot be distinguished from an unused one by reading alone.

Two independent implementations agree on `0x024A` for this firmware:

- **HHD** selects by an explicit version rule — `(major == 1 and ver >= 0x0166) or (major == 2 and
  ver >= 0x0217) or (major >= 3)` → `ADDR_0166`. Our `0x0229` has major 2 and exceeds `0x0217`, so
  HHD uses `rgb = 0x024A`, `m1 = 0x00BA`, `m2 = 0x0163`.
- **HC** reaches the same row by nearest-match on `0x0219`.

Our measured M1/M2 addresses (`0x00BA`, `0x0163`) match that row exactly, so the most coherent
reading is that **`0x0229` is an ordinary member of the new layout** and `0x01FA` retains stale
factory data from the old one. Nothing mixes.

Correspondingly, the earlier claim that proximity matching demonstrably fails on this device is also
retracted — nearest-match and HHD's range rule produce the same, probably correct, answer here.

**To settle it empirically** requires a bounded write trial: set a distinctive colour at `0x024A`,
observe the LEDs, restore. Until then treat `0x024A` as the working base on the authority of two
independent implementations, not on the authority of this dump.

The dump does firmly establish the M-key layout: entries are 8 bytes with `7A`/`7D` magic at
`0x00BA` and `0x0163`. The M1 entry reads `01 00 …`, matching HC's *DirectInput* payload `[01, 00]`,
and byte `0x00BB` is `00` rather than the `04` HC's XInput variant writes — so the device is in its
factory M-key state, consistent with HC not having run since the reboot.

### [HW 2026-08-27] Full command vocabulary

HC's `CommandType` enum is considerably wider than the table above. Values are decimal in source;
hex added here:

| Command | Value | Command | Value |
| --- | --- | --- | --- |
| `EnterProfileConfig` | `0x01` | `SyncToROM` | `0x22` |
| `ExitProfileConfig` | `0x02` | `RestoreFromROM` | `0x23` |
| `WriteProfile` | `0x03` | `SwitchMode` | `0x24` |
| `ReadProfile` | `0x04` | `ReadGamepadMode` | `0x26` |
| `ReadProfileAck` | `0x05` | `GamepadModeAck` | `0x27` |
| `Ack` | `0x06` | `ResetDevice` | `0x28` |
| `SwitchProfile` | `0x07` | `SetFeatureState` | `0x2C` |
| `WriteProfileToEEPRom` | `0x08` | `DisableDevice` | `0x2D` |
| `SyncRGB` | `0x09` | `SetMotionStatus` | `0x2F` |
| `ReadRGBStatusAck` | `0x0A` | `MotionDataAck` | `0x30` |
| `ReadCurrentProfile` | `0x0B` | `RGBControl` | `0xE0` |
| `ReadCurrentProfileAck` | `0x0C` | `CalibrationControl` | `0xFD` |
| `ReadRGBStatus` | `0x0D` | `CalibrationAck` | `0xFE` |

**Caution: the enum and the wire bytes disagree.** `SyncToROM`, `SwitchMode`, `ReadGamepadMode`,
`GamepadModeAck` and `ResetDevice` are emitted from the enum and match this plan's `0x22/0x24/0x26/
0x27/0x28`. But the profile-write byte used on the wire is a hardcoded **`0x21`**, which is not
`CommandType.WriteProfile` (`0x03`). Treat the enum as a partial and partly stale map; trust observed
packets over enum names.

**Two capabilities worth noting that this plan currently assumes away:**

- `SetMotionStatus` (`0x2F`) and `MotionDataAck` (`0x30`) suggest the controller can stream motion.
  **Tested — it does not.** Enabling it produced zero input reports. See the motion section.
- `CalibrationControl` (`0xFD`) / `CalibrationAck` (`0xFE`) expose firmware-side calibration.
  Relevant to the calibration section, which currently assumes all correction is WSGM-side.

Observed packet shapes (all written as 64-byte reports, preamble `0F 00 00 3C`):

```
SwitchMode        : 0F 00 00 3C 24 <mode> <mkeysFunction>
SyncToROM         : 0F 00 00 3C 22
SetMotionStatus   : 0F 00 00 3C 2F <0|1>
```

`SwitchMode` carries a third byte, `MKeysFunction` (`Macro`=0, `Combination`=1) — a mode switch also
sets M-key behaviour, so it is not a pure mode change.

`GamepadMode` values: `Offline`=0, `XInput`=1, `DirectInput`=2, `MSI`=3, `Desktop`=4, `BIOS`=5,
`TESTING`=6. This confirms this plan's XInput `0x01` / DirectInput `0x02` / Desktop `0x04` and adds
`MSI`, `BIOS`, `TESTING`. HC's own default is `GamepadMode.MSI`. HC tracks only PIDs
`0x1901/0x1902/0x1903` and has no knowledge of `0x1904`.

### [HW 2026-08-27] Locate the MCU endpoint by HID usage page, not interface index

The vendor-defined MCU endpoint advertises usage page `0xFFA0`, usage `0x0001`:

```
HID\VID_0DB0&UP:FFA0_U:0001
```

Neither its interface index nor its usage page is stable across controller modes:

| Mode | PID | MCU endpoint location | Usage page / usage |
| --- | --- | --- | --- |
| XInput | `0x1901` | `MI_01` | `0xFFA0` / `0x0001` |
| DirectInput | `0x1902` | `MI_00&COL02` | `0xFFF0` / `0x0040` |

(The XInput layout is observed live. The DirectInput layout is read from retained non-present
`PID_1902` nodes and must be re-confirmed with the unit actually in DirectInput mode.)

Any implementation binding the MCU by interface index breaks on the first mode switch. Select on
usage page and usage instead — which is what HC's `IsReady()` does, filtering candidates on
`Capabilities.UsagePage`/`Capabilities.Usage` rather than interface index.

**The usage page is itself mode-dependent.** HC's `hidFilters` map is keyed by PID:

| Mode | PID | Usage page | Usage |
| --- | --- | --- | --- |
| XInput | `0x1901` | `0xFFA0` | `0x0001` |
| DirectInput | `0x1902` | `0xFFF0` | `0x0040` |

**Both pairs are now confirmed live on this unit** by performing an actual mode switch. HC's filter
table is correct. The selector must be a per-mode `(PID, usagePage, usage)` tuple, not a single
constant — an earlier note in this document claimed one stable usage page across both modes, which
is wrong.

A practical alternative that avoids the table entirely: select the collection whose output report
length is 64 and whose usage page is vendor-defined (`>= 0xFF00`). That identified the MCU correctly
in both modes during testing.

**Enumerate only present devices.** 21 non-present `IG_00`–`IG_14` XInput interface-group nodes are
retained on this unit alongside stale `PID_1902` nodes, consistent with repeated past mode
switching. Endpoint lookup must filter on presence; the "100 mode switch cycles" acceptance test
should also assert something about residue accumulation.

### Request serialization

One serialized state machine owns configuration requests. Match an address only when that response type actually carries one. For generic ACK commands, drain stale input, permit exactly one in flight, then verify through profile readback or device state. A 25 ms profile-ACK deadline from the accepted Linux implementation is the starting measurement, not a blind constant; Windows traces determine the final timeout and retry count.

Mode switch and reset intentionally disconnect/re-enumerate USB. They do not have ordinary in-place completion. The operation completes only when the old interface disappears and the expected interface returns at the same physical USB location, or when a bounded timeout triggers rollback/recovery.

**[HW 2026-08-27] Mode switching verified end to end.** A full XInput → DirectInput → XInput cycle
was performed on the reference unit by writing `0F 00 00 3C 24 <mode> 00` to the MCU endpoint as a
64-byte output report. Both transitions completed and re-enumerated within the polling window, and
the device returned to its original mode and PID cleanly. Notes for the implementation:

- The write path is an ordinary `WriteFile` of exactly `OutputReportByteLength` (64) bytes with
  `buf[0] = 0x0F` as the report ID. No feature report is involved.
- After the switch the MCU endpoint must be re-resolved — both its interface index *and* its usage
  page change (see the endpoint table above). Caching the path across a switch is a bug.
- Completion must be detected by polling for the expected PID at the known physical location, since
  neither container ID nor serial is stable (see the ownership section).
- `SetMotionStatus` (`0x2F 01`) was also written successfully and produced no observable change in
  the Windows sensor stack.

Every MCU operation is lifecycle-tracked even when the protocol has no ordinary completion. Profile reads/writes await their matching response. `Sync to ROM` is serialized against its late/orphan ACK and verified afterward. Switch/reset complete through PnP disappearance/reappearance. No ROM synchronization occurs during ordinary activation unless a real configuration change and captured protocol require it.

## Controller ownership and mapping

### Modes

The supported ownership modes are:

| Firmware mode | Payload | WSGM use                                                   |
| ------------- | ------: | ---------------------------------------------------------- |
| XInput        |  `0x01` | Fallback/restore mode; standard controls and XInput rumble |
| DirectInput   |  `0x02` | Preferred capture mode because it exposes M1/M2            |
| Desktop       |  `0x04` | Never selected for gamepad ownership                       |

Other HC enum values are not selected. Sources disagree on whether PID `0x1903` represents desktop or testing state; the plugin treats labels as untrusted until `Read mode` and descriptors agree. PID `0x1904` is diagnostic-only until confirmed on the unit.

At activation, the Claw plugin reads and stores the current mode. If controller management is enabled and M1/M2 are required, it switches to DirectInput asynchronously, rebinds by the topology key defined below, and reports the physical source ready; WSGM subsequently creates the virtual target and applies HidHide. On deactivation, the plugin closes physical input handles and restores the original mode before WSGM removes HidHide.

The reference unit was in **XInput mode (`PID_1901`)** at first capture, confirming the mode/PID
mapping above.

#### [HW 2026-08-27] Container ID is unusable; key continuation on the USB serial

```
DEVPKEY_Device_ContainerId = {00000000-0000-0000-FFFF-FFFFFFFFFFFF}
```

That is Windows' well-known "not part of a device container" GUID. It is identical on the composite
parent, the MCU endpoint, and the Intel ISS sensor, so it carries no grouping information at all.

This breaks every place the 2.0 plans key continuation on container identity — this document's
activation step 5 and MCU mode-switch completion, and `P1-032` in the implementation backlog
("hotplug/re-enumeration continuation by container identity"). None of it works on this device.

**The USB serial does not work either.** A live mode switch was performed and the instance ID form
changes completely:

```
XInput      USB\VID_0DB0&PID_1901\00006F64096B22E7    <- iSerialNumber
DirectInput USB\VID_0DB0&PID_1902\5&17FBE650&0&2      <- enumeration path, no serial
```

The controller only reports an `iSerialNumber` in XInput mode. In DirectInput it enumerates with a
hub/port instance ID instead. An earlier note in this document proposed the serial as the canonical
continuation key — that is wrong and would fail on the first mode switch.

**The working anchor is the physical USB location**, verified byte-identical before and after a full
switch-and-restore cycle:

| Property | Value in **both** modes |
| --- | --- |
| `DEVPKEY_Device_LocationPaths` | `PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)` |
| | `ACPI(_SB_)#ACPI(PC00)#ACPI(XHCI)#ACPI(RHUB)#ACPI(HS02)` |
| `DEVPKEY_Device_LocationInfo` | `Port_#0002.Hub_#0003` |
| `DEVPKEY_Device_Parent` | `USB\ROOT_HUB30\4&b73bfce&0&0` |
| `DEVPKEY_Device_Address` | `2` |

Key continuation on `LocationPaths` (or parent hub + address). It survives mode switching and
re-enumeration, and it is the only identifier on this device that does.

**[HW 2026-08-27] Two corrections found while implementing the Device Lab inventory sweep.**

1. **HID interfaces carry no `LocationPaths` at all.** The property is `Empty` on every HID child —
   `HID\VID_0DB0&PID_1901&IG_00\…`, `…&MI_01\…`, `…&MI_02&COL01\…` — and first appears **two links
   up** the parent chain, on the USB interface. The full chain measured on this unit:

   | Depth | Device | `LocationPaths` |
   | ---: | --- | --- |
   | 0 | `HID\VID_0DB0&PID_1901&IG_00\8&1717EFAA&0&0000` | *Empty* |
   | 1 | `USB\VID_0DB0&PID_1901&IG_00&4032161&0&00` | *Empty* |
   | 2 | `USB\VID_0DB0&PID_1901&MI_00&2b02ae9f&0&0000` | `PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)#USBMI(0)` |
   | 3 | `USB\VID_0DB0&PID_1901 06F64096B22E7` | `PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(2)` |
   | 4 | `USB\ROOT_HUB30&b73bfce&0&0` | `PCIROOT(0)#PCI(1400)#USBROOT(0)` |

   Since the plugin acquires HID interfaces rather than the composite parent, reading the property
   off the device in hand yields **nothing**. Continuation must resolve it by walking
   `CM_Get_Parent` until a node has it. Use `cfgmgr32` rather than `Win32_PnPEntity`: the WMI route
   needs a `GetDeviceProperties` method call per device per property, and the parent walk has no WMI
   equivalent.

2. **The interface-level path is more precise than is safe.** A resolved HID interface yields
   `…#USB(2)#USBMI(0)`, where the trailing component names *which* interface. A controller mode
   switch rearranges the interfaces — the gamepad is an XInput interface in one mode and a
   DirectInput one in the other — so the interface index is **not** established as stable across the
   very event continuation must tolerate. Only the composite-level prefix `…#USB(2)` was verified
   byte-identical across a full switch-and-restore cycle. **Key on the prefix, keep the full path as
   an observation.** Whether `#USBMI(n)` survives a mode switch is an open question that needs a
   bounded controller-mode trial to answer; until then it is treated as unstable.

`P2-096` should also stop treating container IDs as meaningful identifiers to redact here, and
redact the XInput-mode serial instead.

### DirectInput mapping

Initial mapping from HC, to be captured and tested with simultaneous inputs:

| Physical control | DirectInput source      | Canonical control                             |
| ---------------- | ----------------------- | --------------------------------------------- |
| X / A / B / Y    | Buttons 0 / 1 / 2 / 3   | X / A / B / Y                                 |
| LB / RB          | Buttons 4 / 5           | LB / RB                                       |
| LT / RT digital  | Buttons 6 / 7           | Trigger click metadata only, if useful        |
| Back / Start     | Buttons 8 / 9           | View / Menu                                   |
| L3 / R3          | Buttons 10 / 11         | Left / right stick click                      |
| M1 (LEFT paddle) | Button **16** — byte 7 bit 4 | Rear paddle 1 and OEM channel — **HC has this as 15, which is wrong** |
| M2 (RIGHT paddle) | Button **15** — byte 7 bit 3 | Rear paddle 2 and OEM channel — **HC has this as 16, which is wrong** |
| Left stick       | X / Y                   | Left stick                                    |
| Right stick      | Z / Rotation Z          | Right stick                                   |
| LT / RT analog   | Rotation X / Rotation Y | Left / right trigger                          |

The bring-up matrix specifically tests stick movement plus M1/M2, multi-button rollover, trigger digital/analog duplication, guide-button behavior, dead zones, centers, ranges, and report loss. HC issue #1431 reports concurrent rear-button limitations, so parity with HC is not sufficient evidence.

### [HW 2026-08-27] Raw HID report layout, captured on hardware

The DirectInput gamepad collection (`MI_00&COL01`, usage `0x0001`/`0x0005`) delivers 64-byte input
reports. Neutral state: `01 80 80 80 80 0F 00 00 00 00 …`

| Byte | Contents |
| ---: | --- |
| 0 | report ID `0x01` |
| 1 | left stick **X**, `0x00`–`0xFF`, centre `0x80` |
| 2 | left stick **Y**, centre `0x80` |
| 3 | right stick **X**, centre `0x80` |
| 4 | right stick **Y**, centre `0x80` |
| 5 | low nibble = **D-pad hat**; high nibble = face buttons (bit4 **X**, bit5 **A**, bit6 **B**, bit7 **Y**) |
| 6 | bit0 **LB**, bit1 **RB**, bit2 **LT** digital, bit3 **RT** digital, bit4 *(View/Back — see below)*, bit5 **Start/Menu**, bit6 *(L3 — see below)*, bit7 **R3** |
| 7 | bit3 = a **rear paddle** — see the caveat below |
| 8 | **LT** analog, `0x00`–`0xFF` |
| 9 | **RT** analog, `0x00`–`0xFF` |

D-pad hat is a standard 4-bit HID hat in byte 5's low nibble: `0` up, `2` right, `4` down, `6` left,
`0xF` neutral. Odd values are the diagonals. This is the concrete encoding behind the POV mapping.

Byte 6 is a clean DirectInput button byte — bits 0–7 correspond to buttons 4–11 — which confirms
HC's button indices at the raw-HID level rather than only through the DirectInput API.

#### [HW 2026-08-27] Complete verified button map

Byte 6, confirmed bit by bit — an exact match to DirectInput button indices 4–11:

| bit | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| control | LB | RB | LT | RT | **View/Back** | Start/Menu | **L3** | R3 |
| DInput index | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 |

Byte 7 continues the same progression (bit 0 = index 12):

| bit | 3 | 4 |
| --- | --- | --- |
| control | **M2** — rear paddle, RIGHT | **M1** — rear paddle, LEFT |
| DInput index | 15 | 16 |

#### HC has M1 and M2 inverted

`DClawController` assigns `OEM3 = state.Buttons[15] // M1` and `OEM4 = state.Buttons[16] // M2`.
Measured on the reference unit, **index 15 is the RIGHT paddle (M2) and index 16 is the LEFT paddle
(M1)** — the opposite of HC's assignment.

This was verified with a release-gap between captures, so it is not a hold-over artefact: pressing
the left paddle alone sets byte 7 bit 4, pressing the right paddle alone sets byte 7 bit 3.

Consequence: **do not copy HC's rear-button indices.** A straight port would swap the paddles, and
because M1/M2 are the one class of control this plan permits remapping, the error would surface as
every user's rear-paddle assignments being mirrored. This may also be related to HC issue #1431's
report of concurrent rear-button problems, though that is speculation.

**[HW 2026-08-27] Mapping confirmed against `DClawController`, plus one omission in the table above.**

Every button index above matches HC exactly (X/A/B/Y on buttons 0/1/2/3, LB/RB 4/5, LT/RT digital
6/7, Back/Start 8/9, L3/R3 10/11, M1/M2 15/16). The axis assignment also matches: left stick `X`/`Y`,
right stick `Z`/`RotationZ`, triggers `RotationX`/`RotationY`, with `Y` and `RotationZ` inverted via
a reversed range map.

**The D-pad is missing from the table above.** It is not a button set — it is a POV hat,
`PointOfViewControllers[0]`, in centidegrees:

| Value | Direction |
| ---: | --- |
| `0` | Up |
| `4500` | Up+Right |
| `9000` | Right |
| `13500` | Down+Right |
| `18000` | Down |
| `22500` | Down+Left |
| `27000` | Left |
| `31500` | Up+Left |

Diagonals must set both component directions. Add this row to the mapping table.

Two implementation details worth carrying over:

- **Corrupt first-state guard.** HC discards any state where `RotationX == RotationY ==
  RotationZ == 32767`, treating it as an uninitialised or corrupted read. Worth reproducing — it is a
  real observed failure mode on first acquisition, not defensive noise.
- M1/M2 are assigned with `=` while every other button uses `|=` against injected state. Probably
  incidental, but it means rear buttons cannot be synthesised through HC's injection path.

### Canonical and virtual targets

The plugin publishes only controls physically present on the Claw. Steam Deck Composite receives standard input, rear paddles, and native motion. Its touchpads and stick-touch fields remain unsupported/neutral. Xbox 360 receives standard XInput-compatible controls and no motion. DualShock 4 receives standard controls and native motion where HIDMaestro supports it.

M1/M2 default to the richest target's rear controls. When a selected target lacks rear buttons, the user may assign those OEM controls to a bounded WSGM action or a supported target button. Routing is mutually exclusive: a press is either forwarded as a rear control or consumed as an OEM action, never both. This is the OEM-button exception, not a general remapping surface.

### Rear-button profile memory

The Claw plugin must not rewrite M1/M2 profile memory on every launch. DirectInput should expose buttons 15/16 using the unit's existing profile.

HC, HHD, and the accepted Linux implementation disagree about new-firmware DInput/XInput profile addresses, report lengths, and payload semantics. HC's DInput repair uses a two-byte payload, HHD uses a five-byte form, and XInput addresses may be one byte later. Therefore profile repair is a separate diagnostic operation:

| Firmware | HC DInput M1 / M2 | HC XInput M1 / M2 | Independent conflict |
| --- | --- | --- | --- |
| `0x0211` | `0x007A` / `0x011F` | `0x007B` / `0x0120` | Validate on hardware |
| `0x0217`, `0x0219` | `0x00BA` / `0x0163` | `0x00BB` / `0x0164` | Linux uses `0x00BB` / `0x0164` for its new layout |

HC's DInput candidate payload is `[01, 00]`; HHD uses `[01, 00, 00, 12, 00]`. These are evidence to probe, never defaults selected without reading the current mode/profile and capturing the target's accepted write.

**[HW 2026-08-27] Exact HC packets.** `GetM12` emits different shapes per mode:

```
DirectInput : 0F 00 00 3C 21 01 <add1> <add2> 02 01 00
XInput      : 0F 00 00 3C 21 01 <add1> <add2> 07 04 00 <7A|7D> FF FF FF FF
```

Byte 8 is the payload length (`0x02` DInput, `0x07` XInput). The XInput variant carries `0x7A` for M1
and `0x7D` for M2. HC's full firmware table also stores **separate DInput and XInput addresses** for
each of M1 and M2 — four addresses per firmware, not two:

| Firmware | M1 DInput | M2 DInput | M1 XInput | M2 XInput | RGB base |
| --- | --- | --- | --- | --- | --- |
| `0x163` | `00 7A` | `01 1F` | `00 7B` | `01 20` | `01 FA` |
| `0x166`, `0x167` | `00 BA` | `01 63` | `00 BB` | `01 64` | `02 4A` |
| `0x211` | `00 7A` | `01 1F` | `00 7B` | `01 20` | `01 FA` |
| `0x217`, `0x219` | `00 BA` | `01 63` | `00 BB` | `01 64` | `02 4A` |
| `0x308` | `00 BA` | `01 63` | `00 BB` | `01 64` | `02 4A` |
| `0x411` | `00 BA` | `01 63` | `00 BB` | `01 64` | `02 4A` |

The board comments in HC's table map `0x163–0x167` to `MS-1T41`, `0x211–0x219` to `MS-1T42`/
`MS-1T52`, `0x308` to `MS-1T8K` and `0x411` to `MS-1T91`. **`0x229` appears nowhere.** The XInput
addresses are consistently the DInput address plus one, which matches the "XInput addresses may be
one byte later" note above — but that pattern must not be extrapolated to unknown firmware.

1. Exact firmware descriptor required.
2. Read the current profile and match the returned address.
3. Show the planned change.
4. Write only if M1/M2 are demonstrably absent or the user explicitly requests repair.
5. Read back, sync once if firmware requires persistence, and verify after re-enumeration.

This avoids HC's repeated writes, ROM synchronization, multi-second sleeps, and potential EEPROM wear.

### HidHide transaction

WSGM adds DeviceHost to the HidHide allowlist and records the exact physical-instance entries it owns. During activation it first verifies that DeviceHost can still read the newly hidden physical source, then creates and verifies the virtual target before enabling normal routing. During deactivation it uses the reverse two-phase handoff defined above. Failure at any point reverses only WSGM's changes; external HidHide entries and application lists remain untouched.

Target switching removes one virtual target before creating the next. It never exposes duplicate physical plus virtual input longer than the bounded transition requires.

### WSGM surface input and Steam Input lease policy

The existing Steam Input lease solves a specific problem: Steam's desktop profile can capture the controller from SDL/XInput/DInput/HID, so WSGM temporarily blocks Steam while its overlay or taskbar reads that same controller. Once WSGM controller management is enabled and the Claw plugin owns physical acquisition, that detour is unnecessary. DeviceHost can continuously read the original device because HidHide allowlists it while hiding it from Steam, games, and other ordinary clients.

`UiInputArbiter` therefore has two paths:

| Controller state | WSGM UI input source | Steam surface lease |
| --- | --- | --- |
| WSGM-managed physical source healthy and owned | Canonical physical state from DeviceHost | Never acquired for overlay/taskbar |
| Controller management disabled or externally owned | Existing SDL source | Existing lease behavior, when enabled |
| Unsupported/degraded managed source | SDL fallback after a safe handover | Existing lease behavior, when available |

The decision is based on actual physical-input ownership and health, not merely the presence of a device plugin. TDP/RGB/fan management alone is not proof that WSGM can bypass the Steam lease.

#### Local UI capture

Reading the hidden physical controller is only half of the solution. During normal play its canonical state is forwarded to the selected HIDMaestro virtual target, which Steam or the game can see. When a WSGM-owned focus-taking surface opens, input intended for that surface must not continue into the game through the virtual controller.

The arbiter uses a reference-counted local UI-capture claim for the overlay, taskbar, Settings controller navigation, and any future WSGM surface:

1. Claim UI capture before the surface accepts controller input.
2. Continue reading the physical controller directly.
3. Publish one neutral state to the virtual target, stop forwarding gameplay controls, and stop active rumble.
4. Suppress buttons already held when capture begins until their full release, so the chord/button that opened a surface cannot immediately activate a focused control.
5. Route edge, repeat, chord, and full-state events from the canonical source into WSGM navigation.
6. When the last WSGM surface closes, keep the virtual target neutral until all UI-used controls are released, then resume forwarding the current physical state on a clean boundary.

This is a local WSGM input-capture lease, not a Steam Input lease. It requires no Steam hook installation, HID-handle revocation, Steam controller rescan, device re-enumeration, or layout change.

Steam's native QAM is different: it is a Steam-owned surface and must continue receiving the HIDMaestro virtual controller. Opening native QAM does not claim local WSGM UI capture and does not acquire the Steam Input block lease.

#### Source switching

`GamepadService` should consume an `IUiGamepadSource` rather than owning SDL as its only source. Managed mode uses the DeviceHost canonical source and ignores the matching physical/virtual SDL devices to prevent duplicate presses. Fallback mode retains the current SDL behavior.

Switches are make-before-break where possible:

- Managed source becoming ready while a WSGM surface is open: establish it, suppress currently held controls, begin local capture, then release the Steam Input lease.
- Managed source failing while a surface is open: neutralize the virtual target, acquire the Steam lease, establish SDL input, then release local capture. If fallback fails, preserve keyboard/touch access and do not leak held input to the game.
- Controller management turned off: establish the Steam/SDL fallback, ask the plugin to release physical acquisition, then remove WSGM's virtual target and HidHide entries while DeviceHost continues for noncontroller capabilities.
- Device Integration turned off: establish fallback if possible, then stop the entire device cycle as required by the master toggle.

#### Scope of the remaining Steam lease

The Steam Input lease subsystem remains in WSGM permanently. It is still required for:

- Unsupported devices and external controllers.
- Device Integration or WSGM controller management being off.
- A managed physical input source that is temporarily unavailable.
- Existing per-game launch wrappers where Steam's desktop profile would otherwise capture the HIDMaestro virtual target from a non-Steam program.

Per-game launch leases have a different consumer and lifetime from overlay/taskbar leases and remain supported regardless of managed-device availability. A specific launch may skip a lease only under its own established policy; managed surface input never becomes a reason to delete, uninstall, or globally disable the lease infrastructure.

## Rumble

HIDMaestro output events route through WSGM's output router to `ClawRumbleSink`.

For DirectInput mode, the observed live output is an 11-byte report:

```text
05 01 00 00 <weak 0..255> <strong 0..255> 00 00 00 00 00
```

HC uses this exact-byte path for the A2VM subclass. HHD independently corroborates the shape, but motor order, scale, HID API padding/length requirements, and behavior during simultaneous input still require Windows validation. The 64-byte rule applies to MCU/configuration reports, not automatically to this live-rumble report. XInput fallback uses normal XInput vibration.

**[HW 2026-08-27] Byte order and the A1M gating mechanism confirmed.** `DClawController.WriteVibration`
emits exactly:

```
05 01 00 00 <small * VibrationStrength> <large * VibrationStrength> 00 00 00 00 00
```

So byte 4 is the **small/weak** motor and byte 5 the **large/strong** motor, matching the byte layout
recorded above. `VibrationStrength` is a user scalar applied before send.

The 100 ms binary-rumble workaround is gated on `IDevice.GetCurrent().GetType() == typeof(ClawA1M)` —
an **exact** type comparison. `ClawA2VM` derives from `ClawA1M`, so `GetType()` returns `ClawA2VM` and
the check is false. A2VM therefore already takes the direct path with real 0–255 values, while A1M
gets a background thread that polls every 100 ms and quantises each motor to `193` or `0`. This plan's
instruction not to copy that workaround is correct, and now the mechanism is documented: it is
excluded by exact-type check, not by capability.

Worth noting the A1M path is a genuine latency and fidelity cost — up to 100 ms of delay and a binary
amplitude — so it is also a useful negative example for the output-router latency budget.

Rules:

- Clamp and rate-limit output without adding perceptible latency.
- Coalesce identical samples.
- Send zero to both motors on target removal, game exit, suspend, controller disconnect, plugin disable, and output-router fault.
- Do not copy HC's A1M-only 100 ms binary-rumble workaround.
- Do not claim Steam Deck HD haptics; rich target output degrades to the Claw's verified dual-motor capability.
- Do not alter persistent motor-intensity profile addresses during normal use.

The overlay includes a short left/right/both motor test with an automatic stop timeout.

## Motion sensors

MSI specifies a six-axis IMU. HC acquires it through `Windows.Devices.Sensors`, not through the controller's unused motion command. `ClawMotionSource` follows that proven route first.

### Binding

Enumerate gyrometer and accelerometer devices and bind a stable DeviceId association. Do not silently accept an unrelated system-default sensor on a machine with more than one sensor.

**[HW 2026-08-27] The source is the Intel Integrated Sensor Solution**, as predicted:

| Device | Instance |
| --- | --- |
| HID Sensor Collection V2 | `HID\VID_8087&PID_0AC2\…`, usage page `0x0020` usage `0x0001`, `Status OK` |
| Intel ISS HID Device | `{DEA5AE2A-D1FD-438A-A091-CBD484788436}\VID_8087&PID_0AC2\…` |
| Simple Device Orientation Sensor | `SWD\SENSORSWDEVICEENUMERATOR\SDO#…` |

Note that container association is **not** available — this sensor reports the same null container
GUID as the controller, so binding must key on `DeviceId` alone.

#### [HW 2026-08-27] WinRT enumeration — gyro present, accelerometer absent

> **Tested across both controller modes and with MCU motion explicitly enabled.** The result is
> identical in every combination, so it is a property of the hardware, not an artefact of mode or of
> an unset flag.

```
Gyrometer.GetDefault()          -> DeviceId  \\?\HID#Vid_8087&Pid_0AC2#7&4d8b7c2&0&0000
                                             #{09485f5a-…}\{00760000-0000-0001-0000-000000000000}
                                   MinimumReportInterval = 10 ms      (100 Hz ceiling)
                                   ReportInterval        = 100 ms     (default)
                                   MaxBatchSize = 512, ReportLatency = 0
                                   reading: AngularVelocityX/Y/Z in °/s, DateTimeOffset timestamp
                                   PerformanceCount was empty

Accelerometer.GetDefault()      -> null
Inclinometer.GetDefault()       -> null
OrientationSensor.GetDefault()  -> null
```

Only two sensor-class devices exist on the machine (the ISS collection and a synthetic
`Simple Device Orientation Sensor`), so this is not a wrongly-picked default.

**HC would degrade identically and silently.** `IDevice.PullSensors()` sets the `InternalSensor`
capability when *either* gyrometer or accelerometer is non-null, so the gyro alone selects
`SensorFamily.Windows`. `IMUGyrometer` then binds, while `IMUAccelerometer` gets `null` from the same
`Accelerometer.GetDefault()` call, logs *"not initialised as a Windows"*, and contributes nothing.
That is consistent with the owner's account that HC uses the gyro.

**Three hypotheses were tested and two eliminated:**

| Condition | `Gyrometer` | `Accelerometer` |
| --- | --- | --- |
| XInput, motion flag untouched since boot | present | **null** |
| XInput, after `SetMotionStatus(true)` (`0x2F 01`) | present | **null** |
| DirectInput, motion enabled | present | **null** |

The MCU motion flag makes no difference, and neither does the controller mode. **The A2VM publishes
no accelerometer to the Windows sensor stack.** Question closed.

The consequences are significant: MSI's published "six-axis IMU" is not what Windows exposes, the
Steam Deck and DS4 targets' accelerometer fields stay neutral under this plan's no-synthesis rule,
and any gravity-referenced or fusion feature (Madgwick-style AHRS, orientation, tilt) is
unimplementable from this source. HC's `AcceleroMatrix` for this family is likewise dead
configuration.

**The 100 Hz ceiling is worth noting regardless** (`MinimumReportInterval` 10 ms) — well below native
handheld gyro rates. It belongs in the performance budget and in the target capability report. The
MCU `SetMotionStatus`/`MotionDataAck` path was evaluated as an alternative and **does not stream**:
`SetMotionStatus(true)` (`0x2F 01`) was written and the input pipe monitored for 1.5 s, producing
**zero input reports** and no `MotionDataAck` (`0x30`). `SetMotionStatus(false)` was sent to restore.

That closes the motion question in every direction available: **no accelerometer exists on this
device by any route** — neither the Windows sensor stack nor the controller's own motion command.
Three-axis angular velocity at a 100 Hz ceiling is the complete motion capability of the A2VM.

Use event-driven `ReadingChanged` subscriptions at the nearest supported interval to the requested canonical rate. Never busy-poll. Record source timestamps and translate to a monotonic WSGM time base.

### A2VM orientation

Initial transforms from HC are:

| Sensor | Axis order | Signs |
| --- | --- | --- |
| Gyroscope | X, Y, Z | `+X, +Y, -Z` |
| Accelerometer | X, Z, Y | `canonical X = -source X`, `canonical Y = -source Z`, `canonical Z = +source Y` |

The implementation stores this as an explicit matrix and validates it by rotating/tilting the physical unit around each labeled axis. Canonical units and target conversions are defined once in the controller contract.

### Calibration and target behavior

Allowed processing is limited to sensor correction:

- Stationary gyro-bias calibration.
- Accelerometer zero/scale correction where measured.
- Axis orientation and handedness correction.
- Timestamp/rate normalization.
- Bounded smoothing needed to correct source noise, with raw diagnostics available.

No motion-to-stick or motion-to-mouse mapping exists. Deck and DS4 targets receive native motion data. Xbox mode drops it. Calibration is keyed to the physical sensor identity and invalidated when relevant firmware/device identity changes.

## OEM controls

### Logical controls and defaults

| Logical control | Physical source | Default action |
| --- | --- | --- |
| OEM1 / Claw button | **MSI WMI event low byte `0x29` (41)** — confirmed on hardware | Toggle WSGM overlay |
| OEM2 / Quick Settings, short | **MSI WMI event low byte `0x58` (88)** — confirmed on hardware | Toggle Steam native QAM |
| OEM2 / Quick Settings, **long** | **MSI WMI event low byte `0x2A` (42)** — confirmed on hardware, undocumented elsewhere | User-selected WSGM action |
| OEM3 / M1 | Rear paddle, raw HID byte 7 — see the controller section | Rear paddle 1 / user-selected OEM action |
| OEM4 / M2 | Rear paddle, raw HID byte 7 — see the controller section | Rear paddle 2 / user-selected OEM action |

The right-front button raises its WMI event **and** emits the firmware `Win+G` chord on every press.
Both press durations therefore need chord suppression; the WMI code is the action source and the hook
is suppression-only.

OEM assignments may target a bounded list of WSGM actions, the supported rear control of the current virtual target, or Disabled. No arbitrary executable, PowerShell, text macro, or unrestricted key sequence is accepted.

**[HW 2026-08-27] Codes confirmed on hardware — and there is a third one.**

| Physical action | WMI low byte | Source |
| --- | --- | --- |
| LEFT / Claw button, short press | **`0x29`** (41) | HC `LaunchMcxMainUI`, confirmed on device |
| RIGHT / Quick Settings, short press | **`0x58`** (88) | HC `LaunchMcxOSD`, confirmed on device |
| RIGHT / Quick Settings, **long press** | **`0x2A`** (42) | **new — in no published source** |

Captured across three independent runs, identical each time.

**The long press has its own WMI event — but suppression is still required.** Confirmed by the device
owner: the right-front button emits the firmware `Win+G` chord on **every** press, short or long,
regardless of the WMI event. The WMI codes are raised *in addition to* the chord, not instead of it.

So the division of labour this plan already specifies is exactly right and is now hardware-backed:

- **WMI events are the action source.** `0x58` and `0x2A` distinguish short from long cleanly, with
  a device-identified origin, so WSGM can map the two presses to different actions.
- **The low-level hook is suppression-only.** It exists solely to swallow the `Win+G` side effect and
  must never publish the logical control, because `KBDLLHOOKSTRUCT` cannot identify the source
  keyboard.

The suppressor therefore has to handle the chord on both press durations — the scope is unchanged
from the original design, and `0x2A` adds an action source rather than removing a suppression need.

`onWMIEvent` reads the `MSIEvt` property and masks it with `& byte.MaxValue`, confirming this plan's
"low byte" wording is literally correct.
Subscription is a plain `SELECT * FROM MSI_Event` `ManagementEventWatcher` on `root\WMI`, and
`MSI_Event` reads without elevation on this unit — so the OEM event path needs no privilege.

The names are informative: these are MSI's "launch MSI Center main UI" and "launch MSI Center OSD"
intents, which is why they exist as WMI events rather than HID reports.

After capture confirms it on this A2VM, WMI code 88 is the preferred OEM2 action source. If the MOF/provider event is absent, Raw Input may publish the logical OEM2 event only after it identifies the exact `ACPI\\MSNB1001` device and confirmed sequence. The low-level hook is suppression-only and never publishes OEM2 because it cannot identify the source device. WMI and Raw Input events are timestamped and deduplicated; WSGM alone maps the resulting logical event to the QAM action so one physical press toggles QAM exactly once.

## Firmware `Win+G` and `Win+Tab` suppression

> **[HW 2026-08-27] Both chords captured on hardware. Both are malformed, and that is the key to
> suppressing them without collateral damage.**
>
> ```text
> SHORT press                          LONG press
>   LWin DOWN   t+0.0 ms                 LWin DOWN   t+0.0 ms
>   G    UP     t+5.0 ms  (no DOWN)      Tab  UP     t+6.0 ms  (no DOWN)
>   LWin UP     t+5.0 ms                 LWin UP     t+68.0 ms
> ```
>
> Both originate from `ACPI\MSNB1001`. The long chord is **`Win+Tab`**, as this plan originally
> assumed — an earlier note in this section claiming `Alt+Tab` was wrong and is withdrawn. That
> matters, because `Win+Tab` is a far less-used shortcut than `Alt+Tab` would have been.

### Problem

The right-front Quick Settings button is exposed through the ACPI keyboard device `ACPI\\MSNB1001`.

**[HW 2026-08-27] The actual captured sequence is not what this plan assumed.** Device-identified Raw
Input capture on the reference unit:

```text
[\\?\ACPI#MSNB1001#4&29713d23&0#{884b96c3-...}]
  17:24:13.645   LWin   DOWN
  17:24:13.650   G      UP        <-- no preceding G DOWN
  17:24:13.650   LWin   UP
```

Three properties, each independently useful:

1. **`G` arrives as a key-UP with no matching key-DOWN.** A physical keyboard cannot do this — a
   break code is always preceded by a make code. This is a structural signature, not a heuristic.
2. **The whole burst spans ~5 ms.** No human presses and releases a chord that fast.
3. **Raw Input identifies the originating device** as `ACPI\MSNB1001`.

Earlier revisions of this document recorded the sequence as
`LWin down -> G down -> G up -> LWin up`, i.e. a well-formed chord. That is wrong, and the difference
matters enormously — see below.

Some firmware also associates a long press with:

```text
LWin down -> Tab down -> Tab up -> LWin up
```

If WSGM merely reacts to the button, Windows still opens Xbox Game Bar or Task View. Disabling `i8042prt` blocks the chord but also breaks volume keys delivered by the same ACPI path and requires a reboot. That workaround is prohibited.

### Version-one implementation

Use a small native `WH_KEYBOARD_LL` state machine derived from the proven behavior of the user's merged HC PR, but implemented independently and isolated from the general input manager.

1. Install the hook on a dedicated message-loop thread only while this exact Claw plugin owns OEM input.
2. Track noninjected LWin/RWin, G, **Tab**, Ctrl, Alt, Shift, and other-key-down state — **including, per key, whether a key-down was ever observed**. Keys already held when the hook starts are marked preexisting and passed through until released. Suppress only an **orphan key-up** (`G` or `Tab` up with no matching down) while a Windows key is held and no other modifier is down; a well-formed `Win+G` from a real keyboard passes through untouched, and larger shortcuts such as Ctrl+Win+G are never swallowed.
3. Tag every Claw suppressor/helper `SendInput` packet with an exact `dwExtraInfo` marker and pass tagged events without recursion.
4. On qualifying physical G-down while a Windows key is down, call `SendInput` once with the proven PowerToys-style reserved `VK 0xFF` dummy down/up pair followed by synthetic key-up for each held Windows key. This is one ordered batch, not an atomic operation; never substitute a normal key such as F24.
5. Only after the complete injection succeeds, suppress the G-down, its matching G-up, and the later physical Windows-key releases already synthesized.
6. On zero or partial `SendInput`, use the returned accepted-prefix count to track every inserted dummy transition and every Windows key already released. Clean up only an unmatched injected down state, keep suppression armed for each physical Windows-key release already synthesized, and otherwise fail open without stranding a modifier.
7. The firmware's long action is **`Win+Tab`**, captured as `LWin DOWN, Tab UP, LWin UP`. It has the same orphan-key-up shape as the short chord, so the identical rule handles it — no separate policy, no correlated arming, and no global `Tab` blocking.
8. Reset/unhook on disable, OEM ownership handoff, lock/unlock, suspend/resume, desktop/session change, helper restart, or known message-thread/process failure. Reinstall on known lifecycle transitions; Windows can silently remove a timed-out low-level hook, so the design does not claim a reliable timeout-removal notification.

The callback is strictly bounded and performs only the state transition, at most one `SendInput` batch with its unavoidable reentrant tagged events, and a preallocated bounded-queue write. It never calls the named pipe, WMI, HID, QAM, UI, or synchronous logging.

### [HW 2026-08-27] What the merged HC implementation actually does

`FirmwareWorkarounds.MSI` confirms the mechanism this plan describes:

- `VK_DUMMY = 0x00FF`, the PowerToys reserved no-op key. ✓
- Injected packets tagged with `dwExtraInfo = 0x4843424C`, and tagged events short-circuit at the top
  of the callback (`if (injected && injecting) return true`). ✓
- On qualifying physical G-down with a Windows key held, one `SendInput` batch of
  `[dummy down, dummy up, LWin up?, RWin up?]`. ✓
- **Accepted-prefix accounting**: `releasedLeftWin = leftWinIndex >= 0 && sent > leftWinIndex`, so
  each synthesised Win release is only tracked if `SendInput` actually accepted that record. ✓
- Partial-send cleanup: if exactly one record was accepted (the dummy down), a compensating dummy up
  is sent so no injected down state is stranded. ✓
- Fail-open: if neither Win release was accepted, `shortcutActive`/`gDown` are cleared and the normal
  chord path decides. ✓
- The later *physical* Win key-up is suppressed via the `released` flags, since a synthetic release
  was already delivered. ✓
- `Reset()` clears all tracked state. ✓

**Three gaps against this plan's stricter specification:**

1. **No modifier discrimination.** HC checks only `leftWinDown || rightWinDown` and `Keys.G`. It does
   not inspect Ctrl/Alt/Shift, so **Ctrl+Win+G and Shift+Win+G are swallowed too**. This plan
   requires suppressing only the exact chord "with no other modifier/key down; do not swallow larger
   shortcuts such as Ctrl+Win+G." That requirement is not met by the reference implementation and is
   real added work, not a port.
2. **No preexisting-key handling.** Keys already held when the hook is installed are not marked
   preexisting, so state can start desynchronised. This plan requires pass-through until release.
3. **No Win+Tab path at all.** The long-press variant is unimplemented in HC.
4. **No orphan-key-up discrimination.** HC acts on `args.IsKeyDown` and suppresses any `G` seen while
   a Windows key is held, so it cannot distinguish the firmware chord from a real keyboard's. Our
   design diverges here deliberately — see the source-limitation section.

Also note HC hooks through the `Gma.System.MouseKeyHook` library rather than a raw
`WH_KEYBOARD_LL` callback. This plan's requirement for a bounded, allocation-free native callback
means the library is not a viable dependency — the hook has to be ours.

### Source limitation

`KBDLLHOOKSTRUCT` does not include the originating keyboard device, so a low-level hook cannot identify the *source* of a chord. Raw Input identifies `ACPI\\MSNB1001` but arrives asynchronously and cannot retroactively veto the OS shortcut. HidHide filters HID device visibility, not an ACPI keyboard chord. `RegisterHotKey` is unsuitable for OS-reserved Windows-key combinations. All of that remains true.

**[HW 2026-08-27] But source identification turns out to be unnecessary, because the firmware chord is structurally malformed.**

Both captured chords deliver the second key as a **key-UP with no preceding key-DOWN**. A physical keyboard cannot produce that — a break code is always preceded by a make code, and the OS keyboard stack has no path that emits an orphan break for a key that was never pressed. The condition *"`G`/`Tab` key-up arrives while a Windows key is held, and this hook has seen no corresponding key-down"* is therefore a **sound discriminator**, not a heuristic.

Timing corroborates it independently but is not needed: the short chord completes in ~5 ms, far below any human press-release.

**Third-party corroboration: Microsoft PowerToys' Keyboard Manager cannot remap this chord**, and the
malformation is why. Remapping infrastructure matches on a well-formed make/break pair; an orphan
break never forms a chord it recognises, so the shortcut passes through unremapped. This is useful
evidence in three ways:

- It independently confirms the malformation is real and behaviourally significant, not a capture
  artefact of one tool.
- It explains the design lineage — HC could not simply delegate to PowerToys and had to hand-roll the
  `SendInput` neutraliser, borrowing only the reserved `VK_DUMMY 0xFF` technique from it.
- It rules out an entire class of "just use an existing remapper" solutions for this button. Anything
  that assumes a well-formed chord will not see this one.

Our approach works precisely *because* it does not assume well-formedness — it keys on the anomaly
that defeats everything else.

**This overturns the accepted limitation recorded in earlier revisions of this plan.** Version-one behaviour does *not* have to block `Win+G` system-wide. The suppressor can key on the orphan-key-up signature and leave a real keyboard's `Win+G` — which always arrives as down-then-up — completely untouched. The same signature covers the long press's `Win+Tab`.

Consequences for the implementation described above:

- The state machine must track, per key, whether a down was observed. Suppress the up **only** when no down was seen while a Windows key is held.
- A well-formed `Win+G` from any keyboard passes through untouched. The overlay no longer needs to warn that `Win+G` is globally blocked.
- The `Win+Tab` long press is handled by the identical rule, so it needs no separate policy and no correlated arming.
- HC's merged workaround does **not** implement this check — it acts on `args.IsKeyDown` for the down branch and suppresses any `G` while Win is held. Our implementation diverges here deliberately.

**[HW 2026-08-27] The volume keys prove why the discriminator must be shape-based, not device-based.**
Captured from the *same* `ACPI\MSNB1001` device:

```text
17:26:30.781   VolumeUp     DOWN
17:26:30.817   VolumeUp     UP      (36 ms — well-formed)
17:26:31.583   VolumeDown   DOWN
17:26:31.603   VolumeDown   UP      (20 ms — well-formed)
```

So one ACPI keyboard device carries both the OEM button chords and the volume keys, and the volume
keys are **ordinary well-formed presses**. Two consequences:

1. This is direct evidence for the existing prohibition on disabling `i8042prt` or the ACPI keyboard
   device — doing so kills volume control, which is exactly the regression HC issue #1453 reports.
   The same objection applies to any future device-scoped filter driver bound to `MSNB1001`: it would
   have to inspect event shape anyway, or it breaks volume.
2. **The volume keys must pass through untouched, and with a shape-based rule they do so
   automatically** — they are never orphan key-ups, so the suppressor never examines them beyond the
   key-identity check. No allowlist, no special case.

Residual risk to validate: whether any legitimate software (remapper, macro tool, remote-desktop
client, on-screen keyboard) synthesises orphan key-ups that would now be swallowed. Injected events
are already excluded by the `dwExtraInfo` tag check and the `LLKHF_INJECTED` flag, which covers most
of that surface. Note the suppressor only ever considers `G` and `Tab` while a Windows key is held,
so the exposed surface is two keys under one modifier — not general keyboard filtering.

**[HW 2026-08-27] WMI-correlated arming is no longer required.** Earlier revisions proposed arming
suppression only inside a window opened by the WMI event, as a way to avoid blocking real keyboard
input. The orphan-key-up signature achieves the same result structurally, without depending on event
ordering, so the correlation machinery can be dropped from the design.

The WMI codes remain the **action source** — `0x29` OEM1, `0x58` OEM2 short, `0x2A` OEM2 long — because
they are device-identified and distinguish short from long, which the keyboard chord alone cannot.
The hook remains suppression-only.

Capture tooling: `ChordFinder.exe` performs the device-identified Raw Input capture used to obtain the
sequences above. It is the reference tool for re-validating chord behaviour on new BIOS revisions.

Still worth measuring across supported BIOS versions, cold boot, sleep/resume, and repeat presses:
whether the orphan-key-up shape is stable, and whether any BIOS emits a well-formed chord instead. If
a future BIOS emits `G` with a proper down, the signature stops matching and the suppressor must fail
open rather than fall back to blocking globally.

**[HW 2026-08-27] A kernel filter driver is no longer the "clean fix" this section previously implied.**
Earlier revisions treated a signed upper keyboard filter bound to `ACPI\\MSNB1001` as the only truly
correct solution, with the user-mode hook as a compromise. The captures invert that judgement:

- A device-scoped filter would see the volume keys too, and would still need to inspect event shape to
  avoid breaking them — so it solves nothing the user-mode rule doesn't already solve.
- The orphan-key-up rule is sound rather than heuristic, so the user-mode hook is not a compromise on
  correctness. Its real limitations are elsewhere: it cannot reach elevated foreground windows unless
  the host is itself elevated (it is — see the process-boundary section), and Windows can silently
  remove a timed-out hook.

A filter driver remains out of scope for the first release, but now for the ordinary reason that it
adds a kernel driver, signing pipeline, recovery strategy and input-loss risk for no correctness gain
— not because the user-mode path is second-best.

### Correctness invariants

- Every injected key-down has a matching key-up.
- The plugin never suppresses Win plus keys other than exact confirmed G and, conditionally, Tab sequences.
- Win alone, Alt+Tab, volume keys, OEM1, M1, and M2 remain unaffected.
- No Game Bar or Start-menu flash occurs on a successful short OEM2 press. No Task View flash is required only after Win+Tab is confirmed and enabled.
- Exactly one QAM action occurs per press, hold, or configured repeat policy.
- Hook work remains comfortably below `LowLevelHooksTimeout`.
- Secure desktop is not intercepted; all state is reset on desktop transitions.

## RGB lighting

### Firmware gate

Initial exact descriptors from HC, consistent with the old/new generation split in the Linux driver, are:

| Controller `bcdDevice` | RGB profile base | Status                                        |
| ---------------------- | ---------------- | --------------------------------------------- |
| `0x0211`               | `0x01FA`         | Old A2VM layout; hardware validation required |
| `0x0217`               | `0x024A`         | New A2VM layout; hardware validation required |
| `0x0219`               | `0x024A`         | New A2VM layout; hardware validation required |
| **`0x0229`**           | **`0x024A`**     | **[HW 2026-08-27] reference unit — confirmed by writing and observing the LEDs** |

**[HW 2026-08-27] The reference unit runs firmware newer than any source describes.**

```
DEVPKEY_Device_HardwareIds = USB\VID_0DB0&PID_1901&REV_0229
```

`0x0229` postdates every `bcdDevice` documented by HC, HHD, or the Linux series. Consequences:

**Resolved on hardware.** `ReadProfile` (`0x04`) dumps profile memory at any address, and a write test
confirmed which block drives the LEDs:

- RGB base is **`0x024A`** — confirmed by writing test colours and observing the LEDs respond.
- M1/M2 are **`0x00BA`/`0x0163`** — read directly from profile memory.

All three match the `0x217`/`0x219` row, so **`0x0229` is an ordinary member of the new layout**.
HHD's version rule (`major == 2 && ver >= 0x0217` → `ADDR_0166`) covers it correctly, and HC's
nearest-match arrives at the same row. An intermediate revision of this document claimed the firmware
mixed old and new layouts, inferring the RGB base from which block was populated; the write test
disproved that and the claim is withdrawn.

The device also retains an RGB-shaped block at the *old* base `0x01FA` carrying factory rainbow data
(4 frames, brightness 100). It is inert. **A reader cannot distinguish live from stale by inspection**
— populated-versus-zeroed proves nothing, because a zeroed block is also what "lights off" looks
like. Only a write test distinguishes them.

That is the durable lesson for Device Lab: address discovery for this family needs either a version
rule from a trusted catalog **or** a bounded write-and-observe trial. Reading alone is not sufficient,
and neither is structural shape-matching.

### Physical model

The protocol exposes nine color zones: four LEDs around one stick ring, four around the other, and the ABXY/button group. The bring-up tool confirms physical zone order with a one-zone-at-a-time test before the production UI names them.

**[HW 2026-08-27] HC's claimed zone order and full frame layout.** `GetRGB` builds:

```
0F 00 00 3C        preamble
21 01              write profile 1
<add1> <add2>      RGB base address from the firmware table
20                 write 32 bytes
00 01 09 03        index=0, frameCount=1, effect=0x09, speed=0x03
<brightness>       clamped 0..100
<R,G,B> x 9        nine zone triplets
```

HC's own comment gives the zone order as **right = 0,1,2,3 / left = 4,5,6,7 / buttons = 8**, and its
two-colour mode sends `SecondaryColor` to indices 0–3 and `MainColor` to 4–8. That is a documented
hypothesis to verify with the one-zone-at-a-time test, not a confirmed physical mapping — and note
the button group travels with the *left* colour, which is an odd grouping worth checking.

Brightness is a direct `0–100` byte, not a scaled value. Effect `0x09` and speed `0x03` are the
solid-colour constants; the plan's speed encoding `20 - requestedSpeed` from the Linux work is a
different path and the two have not been reconciled.

**This is exactly where firmware `0x229` bites.** `add1`/`add2` come from `FirmwareDevice.RGB`, which
is the nearest-match lookup. There is no `0x229` row, so any HC-derived base address here is a guess
by proximity. No RGB write can be implemented from this evidence.

### Locked scope: three logical zones, not nine LEDs

**WSGM exposes exactly three lighting zones. Per-LED control is deliberately not offered.**

| Logical zone | Protocol indices | Source |
| --- | --- | --- |
| **Right Ring** | 0, 1, 2, 3 | measured 2026-08-27 |
| **Left Ring** | 4, 5, 6, 7 | measured 2026-08-27 |
| **Buttons** | 8 | measured 2026-08-27 |

The wire format still carries nine independent RGB triplets; the plugin replicates each logical
zone's colour across that zone's indices when building the frame. The per-index order established by
the zone-order capture is therefore an **implementation detail of the encoder**, not a UI surface,
and the ring start-corner asymmetry (right begins bottom-left, left begins top-right) never reaches
the user.

Note this is deliberately *not* HC's grouping. HC's two-colour mode sends its secondary colour to
0–3 and its primary to 4–**8**, i.e. the button group travels with the left ring. WSGM separates the
buttons as their own addressable zone, which the protocol supports natively since all nine triplets
are independent.

Consequences worth carrying into the capability schema:

- Three colour capability instances plus one brightness, not nine — a materially smaller surface for
  the semantic descriptors, the overlay, and per-profile persistence.
- No zone-picker UI, no ring diagram, no per-LED preview. A future device with a different LED count
  changes its own index grouping without changing the semantic contract.
- If a later device genuinely warrants per-LED control, that is a new capability, not an expansion of
  this one.

The capability model supports:

- Off.
- Solid colour across all three zones.
- Independent colour per zone (Right Ring / Left Ring / Buttons).
- Brightness from 0–100, global across zones.
- Experimental breathe, chroma, rainbow, and frostfire patterns after each generated frame sequence is validated on Windows.
- Experimental MCU frame-playback speed 0–20 where supported.

HC's current "Ambilight" is a fixed two-color grouping, not screen-reactive ambient lighting. WSGM calls it a dual-zone/grouped-color mode unless a real reactive protocol is later implemented.

### Write policy

Profile writes begin with the confirmed `0F 00 00 3C 21` framing and include nine RGB triplets. Accepted Linux work constructs the named effects as one-to-eight frame patterns in the same profile/state structure and uses speed encoded as `20 - requestedSpeed`; they are not separate effect opcodes. Returned frame count is treated as untrusted until bounded and validated because some firmware returns garbage values.

Lighting changes are coalesced and committed once when the user applies a setting. Dragging a color or brightness control updates the preview UI, not firmware on every pointer event. Hardware effects animate in the controller; the Claw lighting capability does not stream frames.

**[HW 2026-08-27] RESOLVED: the profile write is persistent, and no `Sync to ROM` is required.**
Confirmed by the device owner — RGB state survives a full reboot. The bring-up tool writes only the
`0x21` profile command and never issues `0x22`, and HHD's `set_rgb_cmd` likewise omits any ROM sync,
so persistence is inherent to the profile write itself rather than a consequence of an explicit
commit.

**There is no known volatile apply path.** Earlier revisions hoped one existed and planned to prefer
it. Every RGB change must therefore be treated as a **non-volatile write**, with these consequences:

- **Coalescing is mandatory, not a nicety.** Dragging a colour wheel or a brightness slider must
  update the preview in the UI only and issue exactly one device write when the value settles. A
  naive per-pointer-event implementation would perform thousands of flash writes in a single
  interaction. This is now a wear-and-lifetime constraint, not a performance one.
- **No startup writes, ever.** Activation must not reapply lighting "to be sure" — the device already
  holds the last committed state.
- **No animation by streaming frames.** Effects run on the MCU from the committed profile; WSGM never
  drives per-frame updates.
- **Lighting is device-persisted desired state.** WSGM stores the user's choice for its own UI, but
  the authoritative value lives on the controller and survives WSGM being uninstalled.
- Deactivation/handoff should **not** rewrite lighting back to a snapshot as a matter of course. A
  deliberate user choice is meant to persist; restoring a pre-WSGM snapshot on exit would silently
  undo it. Snapshot/restore stays a *diagnostic* affordance (as the bring-up tool uses it), not
  lifecycle behaviour.

Readback verification is available and cheap — `ReadProfile` (`0x04`) reads the committed block back
directly — so every write can be confirmed. Unknown firmware still remains read-only.

Because the original state *can* be read reliably, the earlier caveat about being unable to capture
it no longer applies. The stronger reason not to restore lighting on handoff is the one above:
persistence means the user's choice is the intended end state.

## Optional secondary capabilities

The Claw transport exposes additional leads such as a battery charge threshold at data address `0xD7` and firmware shift/scenario state. These fit the generic WSGM capability model, but they ship only after exact `MS-1T52` read/write, AC/DC, MSI Center, suspend, and restore behavior is validated.

They do not block the requested TDP, fan, lighting, controller, rumble, motion, or OEM-button milestone.

## Overlay, Settings, and QAM

### Settings

Settings contains only WSGM ownership/configuration:

- Device integration master toggle.
- WSGM controller-management toggle.
- Plugin trust/update policy.
- Startup behavior and diagnostics/logging level.

It does not contain TDP, fan, lighting, controller-target, calibration, or button controls.

### WSGM overlay Device destination

This destination is available in Desktop Mode and Game Mode whenever device integration is enabled and the overlay can be opened. It remains the authoritative control surface even when Steam is not running or its QAM patch is unavailable.

| Section | Controls |
| --- | --- |
| Overview | Device identity, ownership, capability health, profile, fan RPM, conflicts, and live temperatures only when a separate source is validated |
| Power and thermals | PL1/TDP, PL2 boost, profile, fan mode, dual curves, full-speed override |
| Controller | Target, physical/virtual status, input test, HidHide status, rumble test |
| Motion | Sensor identity, live axes, rate, bias/calibration, target support |
| OEM controls | OEM1/OEM2/M1/M2 actions, firmware Win+G blocker and limitation |
| Lighting | Effect, three zone colours (Right Ring / Left Ring / Buttons), brightness, speed, apply/revert — no per-LED control |
| Diagnostics | Firmware/descriptors, WMI/provider state, snapshots, last transactions, trace export |

The Controller section also reports and previews the physical handheld glyph profile used by Steam CEF and WSGM's own controller surfaces. `msi.claw` from the pinned Handheld Controller Glyphs catalog is the initial A2VM candidate, not an assumed match. It becomes the automatic default only after the full/left/right artwork, MSI Center and QAM front-button sides, and M1/M2 rear-button sides are verified on the A2VM. A mismatch creates a distinct `msi.claw-a2vm` profile instead of borrowing misleading artwork.

### Native Steam QAM

The native QAM is a Game Mode projection of the same long-lived device state. Opening, closing, injecting, or reconnecting the QAM never starts or stops the plugin.

The native Steam QAM duplicates only high-frequency gameplay controls:

- PL1/TDP slider and current value.
- Active performance profile.
- Frame limit and RTSS performance-overlay level from their shared services.
- Virtual controller target.

The right-front OEM2 button calls the allowlisted `SteamUiHost.ToggleQuickAccess` action. The WSGM overlay retains the same power and target controls plus all deeper Claw controls. Both surfaces observe one capability state and one command implementation.

The independent Steam Input handheld-glyph CEF patch presents that right-front control with the MSI QAM glyph and the rear controls as M1/M2, matching the physical Claw rather than Valve's default Steam Deck or Xbox artwork. This presentation does not alter the firmware Win+G suppression path, logical OEM mapping, or HIDMaestro target. If its selector fingerprint becomes incompatible, only the glyph patch falls back to native Steam rendering.

## State, profiles, and persistence

Separate three kinds of state:

| State kind | Examples | Rule |
| --- | --- | --- |
| Captured hardware state | Original fan tables, flags, controller mode, scenario | Restore on ownership release when safe |
| WSGM desired state | Selected profile, TDP policy, fan curve, RGB, OEM actions | Persist under the stable device identity |
| Per-application override | TDP/profile and virtual target | Apply after application detection; remove back to global state |

The recovery journal includes the device identity, host generation, captured state, changes successfully applied, and cleanup status. It is updated atomically before and after each ownership-changing transaction. On a crash, the next host compares live state and offers or performs only a proven safe restoration.

## Reliability and safety rules

- Hardware writes fail closed; input forwarding fails open when doing so avoids a stuck or unusable device.
- Each transport has one serializer, cancellation, bounded retries, and circuit breaking.
- No write is made if its exact prerequisite read failed.
- No firmware address is selected by numerical proximity.
- No operation relies on unbounded `Sleep`; waits observe ACKs, PnP events, WMI events, or deadlines.
- A removed/re-enumerated interface invalidates every previous handle and generation.
- Rumble always has an explicit stop path.
- Custom fan control has an exact restore path.
- A critical transport error removes the affected capability without crashing WSGM, freezing the desktop overlay, or blocking a mode transition.
- CEF, overlay, and QAM code cannot issue raw hardware operations.
- Secrets, raw memory, full device paths containing unique IDs, and high-rate samples are redacted or opt-in in exported diagnostics.

## Performance budget

The WSGM 2.0 controller path must not repeat the observed 4–6% CPU cost of the current VIIPER Steam Deck target setup.

Release goals measured on the Claw 8 AI+ A2VM are:

- No fixed high-rate work when device integration is disabled.
- DeviceHost plus plugin below 0.5% average CPU while active but controller/IMU idle.
- Full controller plus motion path below 2% average CPU during representative gameplay, excluding game/Steam/RTSS cost.
- Zero allocation/busy-spin loop in the low-level keyboard hook.
- No periodic MCU profile writes.
- WMI telemetry at a bounded low rate, reduced or stopped when the overlay/QAM does not consume it.

These are acceptance targets, not reasons to hide measurements. Benchmarks record package power, CPU time by thread, wakeups/context switches, report rate, dropped/coalesced samples, pipe/shared-memory cost, and HIDMaestro target cost separately.

## Diagnostics and bring-up tooling

Claw bring-up uses the [device plugin system and developer tooling](./device-plugin-system-and-tooling.md). Device Lab first inventories the unit, ranks known MSI/Claw implementation modules, runs their dedicated safe compatibility probes, and generates the exact `MS-1T52` scaffold. Protocol analysis is used only for the remaining incompatible or inconclusive modules.

The Claw plugin exposes a versioned read-only diagnostics surface that Device Lab can capture without stopping or recreating the process-long device lifecycle:

- SMBIOS and exact board gate result.
- USB container, interfaces, descriptors, report descriptors, endpoints, and `bcdDevice`.
- Current/read controller mode and mode/PID mapping.
- MSI WMI provider/class/version and supported method results.
- PL1/PL2, fan curve buffers, RPM, flags, scenario snapshots, and any separately validated live-temperature source.
- Windows sensor DeviceIds, report intervals, units, axes, and timestamps.
- WMI/Raw Input/hook timing for OEM buttons with actual text input redacted.
- HID configuration request/ACK metadata with unique identifiers removed.
- Current plugin-owned resource state and recovery-journal status.
- Per-component CPU, sample rates, queue depth, timeouts, and dropped events.

Device Lab combines that plugin stream with separate WSGM-owned HidHide, virtual-target, input-arbiter, desired-profile, and command-routing diagnostics.

Write-capable probes require an explicit action, show the exact capability and rollback, and never combine unrelated experiments.

## Implementation milestones

### M0: hardware characterization and bounded compatibility

Read-only bootstrap:

- Freeze the initial private-capture, sanitized-export, evidence, fixture, known-module, and scaffold schemas.
- Register the known MSI WMI, MCU, controller, power-policy, fan, lighting, motion, and OEM-event candidates with exact provenance.
- Bootstrap the reviewed MSI inventory and read-probe modules needed by the candidate engine.
- Run the automatic MSI compatibility sweep and dedicated read probes on the reference unit.
- Generate an initial compiling `MS-1T52` detector, manifest, evidence lock, and fail-closed scaffold; capabilities without implemented verified modules remain unavailable.
- Capture controller interfaces/descriptors in every safe existing mode.
- Identify `bcdDevice`, controller firmware, WMI provider, sensor IDs, and OEM event timing.

Explicit bounded compatibility:

- Capture MSI Center M before/after state for TDP, fan, and one lighting change.
- Prove Windows-provider PL1/PL2 reads and readback; a working setter alone is insufficient.
- Map every A2VM shift/scenario mode's PL1/PL2 ceilings on AC and battery.
- Resolve the six-versus-eight fan-table discrepancy and all address differences.
- Run each required power, fan, rumble, lighting, and controller-mode trial separately through Device Lab with verified restoration.
- Regenerate the exact module composition and capability registrations only after the corresponding modules and evidence qualify.
- Produce golden binary fixtures and sanitized traces.

Exit gate: no unknown write layout remains in a capability scheduled for M1–M4.

### M1: lifecycle, ownership, and OEM safety

- Implement unelevated per-plugin DeviceHost supervision, semantic IPC, state quality, capability negotiation, and any separately justified Claw-helper protocol.
- Start device integration with WSGM in Desktop or Game Mode and keep one host generation across shell-mode transitions.
- Implement detection, passive/conflict state, snapshots, recovery journal, suspend/resume, clean disable, and clean WSGM-exit teardown.
- Implement WMI OEM1/OEM2 events and M1/M2 logical events.
- Implement and validate the `Win+G` blocker, deduplication, QAM action, and volume-key preservation.

Exit gate: Desktop Mode exposes complete device control without requiring Steam; mode transitions do not reinitialize hardware; plugin off/WSGM exit leaves zero hooks/handles/writes; OEM2 toggles QAM once without Game Bar, stuck keys, or broken volume controls.

### M2: power and thermals

- Implement serialized WMI transport.
- Ship PL1/PL2 state, scenario compatibility, validation, readback, rollback, profiles, and QAM TDP binding.
- Ship automatic/custom/full-speed fan modes, two channel curves, RPM, snapshot/restore, and safety validation; add live temperature only through a proven source.

Exit gate: AC/battery, error injection, conflict, and 100 suspend/resume cycles preserve safe limits and fan ownership.

### M3: controller, rumble, and motion

Plugin work:

- Implement DirectInput/XInput capture and canonical mapping.
- Implement transactional physical mode switching, M1/M2, physical rumble sink, sensor binding, calibration, and canonical native-motion publication.

WSGM work:

- Integrate HIDMaestro Steam Deck, Xbox 360, and DualShock 4 targets.
- Implement HidHide transaction ownership and canonical output routing to the plugin.
- Implement `IUiGamepadSource`, managed physical navigation, reference-counted local UI capture, virtual-target neutralization, and the SDL/Steam-lease fallback.
- Measure report rates and CPU; fix simultaneous rear-button/input behavior.

Exit gate: managed surfaces use no Steam lease, UI input never leaks through the virtual target, fallback transitions are lossless, native QAM still receives virtual input, all target acceptance tests pass, output stops cleanly, 100 mode switches pass, and performance budgets hold.

### M4: lighting and rare persistent profile operations

- Validate RGB zone order, effects, speed, brightness, old/new descriptors, ACK flow, readback, and persistence.
- Ship coalesced lighting commits.
- Add rear-button profile repair only if actual hardware requires it.

Exit gate: no unknown-firmware writes, no redundant ROM syncs, and power-loss/re-enumeration tests preserve a usable controller.

### M5: release hardening

- Complete MSI Center/HC handoff testing.
- Complete crash, forced termination, user switching, lock, hibernate, update, and rollback tests.
- Audit installer prerequisites, provider redistribution, driver notices, resource/risk declarations, optional Claw helper, and third-party attribution.
- Stabilize, version, and publish the capture/scaffold formats and contributor template created during M0.

## Acceptance matrix

### Detection and negative testing

- Exact `MS-1T52` with each supported controller firmware.
- Unknown `MS-1T52` controller firmware: standard input/read-only state works; profile/RGB writes remain disabled.
- `MS-1T42`, `MS-1T41`, unrelated MSI PCs, spoofed VID/PID, and missing SMBIOS signal do not activate this descriptor.
- Missing/corrupt WMI provider degrades WMI capabilities without altering ACPI or blocking Game Mode.

### Power and fans

- Every PL1 endpoint 8/30 W, PL2 endpoint 8/37 W, equality, invalid combinations, rapid slider changes, and readback mismatch.
- AC/DC transitions, per-game apply/remove, profile changes, MSI scenario state, resume, and provider timeout.
- Independent left/right table edit and restore, automatic/custom/full-speed transitions, tach zero/nonzero, monotonic validation, hottest-point floor, and one-channel failure rollback.
- Fan command verification gates on **table readback, never on RPM** — the fans take tens of seconds to converge, so any RPM-based assertion must allow for that or it will be flaky.
- Host crash and restart with each fan mode active.

### Controller and virtual targets

- All controls, diagonals, full analog ranges, guide, simultaneous stick+M1/M2, and multi-button rollover.
- DirectInput/XInput re-enumeration and 100 switch cycles.
- Steam Deck recognition and native motion/rear buttons.
- Xbox 360 native XInput behavior in representative older software/emulators.
- DualShock 4 acceptance by official PlayStation Remote Play.
- Target change while Steam/game is open, player-slot behavior, duplicate-input absence, and HidHide rollback.
- Controller unplug/reappear, helper crash, WSGM restart, Steam restart, suspend/resume, hibernate, and forced game exit.

### WSGM UI input and Steam lease

- Managed controller healthy: open/close overlay, taskbar, Settings, and overlapping/handover surfaces without acquiring a Steam Input lease.
- While a WSGM surface owns local capture, every virtual target reports neutral gameplay controls and the background game/Steam UI receives no navigation press.
- The opening chord and closing/back button remain suppressed until full release and never leak on either boundary.
- Multiple WSGM surfaces reference-count local capture; closing one cannot resume virtual forwarding while another still owns it.
- Steam native QAM receives the virtual controller normally and never claims WSGM local capture.
- Managed source activation/failure, controller-management toggle, and Device Integration master toggle during an open surface follow the make-before-break/fallback rules.
- Direct source plus SDL cannot emit duplicate navigation events from the physical and virtual representations of the same controller.
- Unsupported/external controllers retain the current Steam lease and SDL behavior.
- Per-game launch leases remain independent and continue to protect directly launched programs until their separate target matrix passes.
- Logs and native status confirm zero Steam HID-handle revocation/recovery churn for managed overlay/taskbar opens.

### Rumble and motion

- Weak/strong motor identity, min/max/combined values, high-frequency changes, coalescing, and output stop on every lifecycle failure.
- Gyro and accelerometer sign/axis tests for all six directions, stationary bias, rate/timestamp monotonicity, drift, sleep/resume, and sensor disappearance.
- Xbox target receives no synthetic gyro mapping.

### OEM and firmware shortcuts

- OEM1, OEM2, M1, and M2 tap/hold/repeat, double press, and simultaneous gamepad input.
- Current and minimum-supported BIOS/controller firmware.
- WMI event/code ordering versus Raw Input and hook timestamps.
- Game Bar installed/enabled, disabled, and absent.
- Short Win+G and, only after target confirmation, the long Win+Tab sequence.
- **External-keyboard Win+G and Win+Tab must remain fully functional** while suppression owns the firmware chord — a well-formed down-then-up chord is never swallowed. This is the acceptance test for the orphan-key-up discriminator and replaces the earlier "globally blocked" expectation.
- Orphan-key-up injected by unrelated software (remapper, macro tool, remote desktop, on-screen keyboard) is not swallowed, given the injected-flag and `dwExtraInfo` exclusions.
- Key auto-repeat; both Windows keys held; Ctrl/Alt/Shift interleavings; another key pressed mid-transaction; hook activation while Win is already held; and tagged/untagged injected Win/G events from other applications.
- Hook callback reentrancy, `SendInput` accepted-prefix counts from zero through the full batch, and Claw-helper/hook-thread failure after every possible inserted packet.
- Win alone, Win+other keys, Alt+Tab, volume up/down/mute, and unrelated OEM controls remain normal. Volume keys arrive from the same `ACPI\MSNB1001` device as the suppressed chords and are well-formed, so they must be verified explicitly as unaffected.
- Steam Big Picture, desktop, windowed, borderless, exclusive fullscreen, and elevated foreground applications.
- Cold boot, lock/unlock, sleep, hibernate, resume, sign-out/in, user switch, helper crash/restart, and secure-desktop transitions.
- Device integration off, or OEM-suppression ownership explicitly handed to HC: Claw suppression hook absent and native Win+G restored when no other manager blocks it.
- After every suppressed firmware-chord case, LWin, RWin, G, and conditionally Tab are logically up; no Game Bar, Start menu, or applicable Task View flash remains.
- Exactly one QAM toggle per physical OEM2 action.

### Lighting

- One-zone identification for all nine zones.
- Brightness endpoints, supported effects and speeds, grouped colors, off/on, rapid UI changes, apply/cancel, readback, USB re-enumeration, suspend, power loss, and unknown firmware.
- Confirm no write occurs for every preview movement and no extra ROM sync occurs at activation.

### Coexistence and performance

- Launch WSGM into Desktop Mode with Steam absent: the complete Device overlay works and hardware ownership remains healthy.
- Start/stop Steam and enter/leave Game Mode repeatedly without changing the DeviceHost generation or replaying startup hardware writes.
- HC active before WSGM, launched during WSGM ownership, and started after WSGM handoff.
- MSI Center M active, inactive, updated, and service-only states.
- Existing external HidHide configuration remains byte-for-byte/entry-for-entry intact outside WSGM ownership.
- Idle and active CPU, wakeups, allocations, queue depth, report loss, end-to-end input latency, and one-hour soak.
- Back to Game Mode remains immediate with WMI unavailable, controller re-enumerating, Steam CEF restarting, and device initialization failed.
- Repeated Desktop Mode ↔ Game Mode transitions reuse the same DeviceHost generation, hardware handles where still valid, fan/RGB state, controller mode, virtual target, and OEM ownership.

## Hardware questions that must be closed on the reference unit

Status as of the 2026-08-27 read-only capture:

| # | Question | Status |
| ---: | --- | --- |
| 1 | Controller `bcdDevice`, BIOS, EC, MCU versions | **Mostly closed.** `bcdDevice 0x0229`; BIOS `E1T52IMS.112`; EC `1T52EMS1.109` via `Get_EC`. MCU version still open |
| 2 | Interface/report descriptors and container identity in both modes | **Closed.** Both modes captured live. Container identity unusable (null GUID); serial exists only in XInput. Continuation key is `LocationPaths` / parent+address, verified identical across a switch cycle. Full 64-byte report layout and button/axis map verified — see the controller section |
| 3 | Meaning of PID `0x1903`/`0x1904`, safe read-mode behavior | **Partly closed.** HC names `0x1903` `PID_TESTING` and tracks only `0x1901/0x1902/0x1903`; `GamepadMode` adds `MSI`=3, `BIOS`=5, `TESTING`=6. `0x1904` is unknown to HC. Mode↔PID correspondence still unverified |
| 4 | WMI buffer layout/units for fan points, custom-enable, full-speed, temperatures, tach | **Closed.** Tach: 2 ch, BE divisor, `480000/x`. Temps: six points, bytes 1 and 4–8. Duty: six points, bytes 2–7, direct percent. Custom-enable `0xD4` bit 7; full-speed `0x98` bit 7. Factory curve captured: `0/40/49/58/67/75 %` at `0/50/60/70/80/88 °C` |
| 5 | Whether WMI code 88 exists and precedes the keyboard chord reliably | **Codes closed** (41 `LaunchMcxMainUI`→OEM1, 88 `LaunchMcxOSD`→OEM2, low-byte masked). `MSI_Event` exists and reads unelevated. *Ordering* vs the chord still needs a marked capture |
| 6 | Low-level/Raw Input scan codes, injected flags, repeats, Win+Tab on current firmware | **Closed.** Captured via `ChordFinder.exe` from `ACPI\MSNB1001`. Short = `LWin DOWN, G UP, LWin UP` in ~5 ms; long = `LWin DOWN, Tab UP, LWin UP` in ~68 ms. **Both deliver the second key as an orphan UP with no DOWN** — a sound discriminator, so global blocking is unnecessary. Long chord is `Win+Tab`, not `Alt+Tab` |
| 7 | Stable Windows sensor DeviceId, units, max rate, resume behavior | **Closed for identity and rate.** Intel ISS `VID_8087&PID_0AC2`; `DeviceId` captured; gyro `MinimumReportInterval` 10 ms (100 Hz ceiling), °/s, `MaxBatchSize` 512. **No accelerometer exists** — verified null in both modes and with MCU motion enabled. Resume behaviour still untested |
| 8 | DirectInput live-rumble motor order/scale | **Mostly closed.** `05 01 00 00 <small> <large> 00×5`; byte 4 weak, byte 5 strong. A1M's 100 ms binary workaround excluded by exact-type check, so A2VM already uses real 0–255 values. Physical motor identity still needs a bench test |
| 9 | RGB zone order, effects, profile readback, volatile apply path | **Closed.** Base `0x024A` confirmed by write-and-observe. Frame layout known. Readback via `ReadProfile` (`0x04`). Zone order measured (right 0–3, left 4–7, buttons 8). **Writes are persistent across reboot with no `SyncToROM`; no volatile path exists** — coalescing is mandatory |
| 10 | Old/new M1/M2 profile-address semantics; whether repair is needed | **Closed: M1 `0x00BA`, M2 `0x0163`**, read from the device, 8-byte entries with `7A`/`7D` magic. Device is in factory M-key state; no repair needed |
| 11 | PL1/PL2 ordering requirements and behavior at equal values | **Reads closed** (PL1 30 W, PL2 37 W, PL3 0). Ordering/equality behaviour needs a bounded write trial |
| 12 | MSI WMI provider provenance and redistribution route | **Closed.** OEM-installed on every target system from first boot; nothing is redistributed |

Newly corroborated on `MS-1T52` beyond the original question list: the `0x50`/`0x51`/`0x52`
power addresses, the `0xD2` scenario address and its `0xC4` Sport value, the `0xD4` custom-fan flag,
the `0xD7` charge-limit address (reading 80 %), the `Package_32` 32-byte contract, and the `0x01`
success-status convention.

None of these questions permits a nearest-version write. They determine which capability is enabled for a given firmware descriptor.

### Immediate next steps

Device verification is complete for everything reachable without a person physically at the machine.

**Completed 2026-08-27 on the reference unit:**

- Full HC source sweep — protocol vocabulary for power, fans, telemetry, MCU framing, controller
  mapping, rumble, RGB frames, M1/M2 packets, OEM event codes, and the Win+G injection contract.
- Stock EC capture post-reboot: factory power limits, fan curve, scenario, charge limit.
- Live `MSI_ACPI` register reads, `Package_32` contract, provider version 8.0, EC firmware string.
- HID capability probe of every present endpoint in **both** controller modes.
- WinRT sensor enumeration in both modes and with MCU motion enabled.
- A full XInput → DirectInput → XInput switch cycle, restored cleanly.
- `ReadProfile` protocol discovery and a `0x0000`–`0x02DF` profile-memory dump, which resolved the
  RGB base and M1/M2 addresses that no published table contains.
- Temperature-under-load run confirming `Get_Temperature` sub0 is a live sensor.

*Caveat: `SetMotionStatus` was toggled on and then off. Its pre-existing value was never readable, so
the next clean-state capture should follow a reboot rather than assume current state is pristine.*

**Still needs a bounded trial (automatable, not yet run):**

1. DirectInput button/axis capture: M1/M2 indices, POV hat behaviour, simultaneous-input limits.
2. Rumble on the DirectInput gamepad's 32-byte output report, with a guaranteed zero-output stop.

**Completed with the operator 2026-08-27 — all resolved:**

- RGB zone order — measured; three-zone scope locked.
- Fan channel-to-side — channel 1 = left, channel 2 = right.
- OEM button codes including the undocumented long-press `0x2A`, and both firmware chords captured
  with their orphan-key-up signature.
- Rumble motor identity — byte 4 = right, byte 5 = left.
- Full gamepad button map including the corrected M1/M2 indices.

- RGB persistence — writes survive reboot with no `SyncToROM`; no volatile path exists.

**Remaining open items — none of them change a subsystem's shape:**

- Suspend/resume behaviour of the gyro binding and of hardware state.
- Whether the long press also emits `Win+Tab` when the short press's `Win+G` is already suppressed
  (interaction between the two chords).
- MCU/controller firmware version beyond `bcdDevice`, if one is separately reported.
- Scenario/PL interaction: which PL1/PL2 ceilings each scenario imposes on AC and battery.

## Licensing and reference-use rules

The operative distinction is **facts versus expression**, not project-by-project license anxiety.

Hardware protocol facts — a WMI data address, a report prefix, a method ID, a buffer length, a zone
count — are facts about MSI's hardware. They are not copyrightable subject matter and none of the
projects below can license them to us in the first place. Learning from HC, HHD, the Linux series,
or ClawTweaks *what the hardware does*, then implementing it independently, is not a derivative work
and needs no licensing resolution.

What actually requires care is copying substantial **expression**: lifting packet-builder code,
whole structured register tables, or distinctive code organization verbatim.

- Handheld Companion (CC BY-NC-SA 4.0 README at the audited commit): behavioral and protocol
  reference. Read it for facts, implement independently. Do not paste its code or tables.
- Linux `hid-msi` (GPL-2.0-or-later): same treatment.
- ClawTweaks (AGPL): same treatment; useful mainly as validation leads.
- HHD: same treatment.
- Any **redistributed binary, driver, or provider** is a genuinely different question and does need
  its own license and notice review — that is about shipping someone else's artifact, not about
  reimplementation.
- **[HW 2026-08-27] MSI's provider DLL is not redistributed at all.** It ships with every target
  system from first boot, so the plugin detects it as an OEM prerequisite. No redistribution right
  is required and `P0-040` is closed.
- Keep an implementation evidence log linking each constant to an official source, independent project, hardware capture, or test fixture.
- Supply physical controller artwork through WSGM's pinned Handheld Controller Glyphs catalog rather than the Claw plugin package. WSGM preserves the upstream MIT notice, asset inventory, and credited artwork provenance; the plugin selects only the reviewed profile ID.

## References

### Handheld Companion

- Audited source revision and license notice: https://github.com/Valkirie/HandheldCompanion/tree/5c94abca83f8711ff5620906871b31a41c76bf05
- A2VM device: https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Devices/MSI/ClawA2VM.cs
- A1M transport inherited by A2VM: https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Devices/MSI/ClawA1M.cs
- DirectInput controller: https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Controllers/MSI/DClawController.cs
- WMI helper: https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/WMI.cs
- Firmware workaround helper: https://github.com/Valkirie/HandheldCompanion/blob/main/HandheldCompanion/Helpers/FirmwareWorkarounds.cs
- User-authored merged Win+G fix: https://github.com/Valkirie/HandheldCompanion/pull/1459
- Win+G ACPI capture: https://github.com/Valkirie/HandheldCompanion/issues/1444
- `i8042prt` volume-key regression: https://github.com/Valkirie/HandheldCompanion/issues/1453

### WSGM presentation

- Handheld controller glyph integration: [controller-glyph-integration.md](./controller-glyph-integration.md)

### Independent implementations and primary references

- Accepted Linux `hid-msi` series: https://patchew.org/linux/20260720031549.2272658-1-derekjohn.clark@gmail.com/
- Linux `msi-wmi-platform` discussion: https://www.spinics.net/lists/linux-doc/msg187837.html
- HHD Claw implementation: https://github.com/hhd-dev/hhd/tree/master/src/hhd/device/claw
- HHD MSI WMI implementation: https://github.com/hhd-dev/hhd/blob/master/src/adjustor/drivers/msi/__init__.py
- ClawTweaks: https://github.com/enterTheVoidCode/ClawTweaks
- MSI product specification: https://www.msi.com/Handheld/Claw-8-AI-Plus-A2VMX/Specification
- Microsoft low-level keyboard hook: https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc
- Microsoft `SendInput`: https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-sendinput
- Microsoft Kbfiltr sample: https://learn.microsoft.com/samples/microsoft/windows-driver-samples/keyboard-input-wdf-filter-driver-kbfiltr/

## Definition of done

The Claw 8 AI+ A2VM plugin is complete when a user can control it from the WSGM overlay throughout the full WSGM run in Desktop or Game Mode; navigate WSGM surfaces directly from the hidden physical controller without a Steam Input surface lease or input leaking into the virtual target; press the right-front button in Game Mode to open Steam's native QAM without triggering Game Bar; adjust TDP and common performance controls there; select Steam Deck/Xbox/DS4 presentation; receive rumble and native motion where supported; cross mode boundaries and suspend/resume repeatedly without reinitializing the device; and hand the device back to HC without stale hooks, hidden devices, stuck keys, unsafe fan state, or several-percent idle CPU use.

Turning the entire device system off must return WSGM to its lightweight Steam-focused behavior and leave the Claw under its firmware or external manager's ownership.
