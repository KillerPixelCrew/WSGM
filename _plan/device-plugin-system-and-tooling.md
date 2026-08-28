# WSGM 2.0 Device Plugin System and Developer Tooling

- Status: Design baseline
- Parent design: [WSGM 2.0 design](./2.0-design.md)
- First reference implementation: [MSI Claw 8 AI+ A2VM](./claw-8-a2vm-plugin.md)

## Purpose

This document defines both the WSGM 2.0 device-plugin boundary and the developer tooling used to bring up new handhelds.

The primary developer workflow is not automatic reverse engineering. It automates the work an experienced maintainer already performs when receiving a new device:

1. Inventory the exact machine, firmware, and device topology.
2. Find previously verified implementations from the same family.
3. Reject candidates with hard protocol, firmware, or platform mismatches.
4. Run the known implementations' safe compatibility probes.
5. Identify which transport, protocol, layout, and policy modules can be reused.
6. Guide the maintainer through any bounded confirmation tests still required.
7. Generate a compiling, fail-closed plugin project with the verified basics already filled in.
8. Leave only genuinely new hardware behavior for manual investigation.

For example, a new MSI handheld should first be compared against the known MSI WMI, Claw MCU, controller, fan, lighting, and OEM-button implementations. The tool should not force the maintainer to recreate that comparison manually or begin by scanning unknown EC addresses.

The deeper capture and protocol-analysis tools exist for the remaining gaps. They are not the first step.

## Handheld Companion control-method sweep

The audited HC revision contains 93 device source files, 82 concrete device types selected by its device manager, 12 manufacturer families, and 30 controller implementations. This is an architectural inventory, not a source-code reuse plan; HC remains behavioral and protocol evidence subject to the licensing rules below.

The sweep found these recurring implementation families:

| Area | Mechanisms found across HC devices | Design consequence |
| --- | --- | --- |
| Identity | SMBIOS manufacturer/product/board/revision, CPU family, BIOS/EC/MCU firmware, PnP topology, descriptor and report-shape probes | Match exact device definitions and firmware predicates; marketing names are only weak evidence |
| Embedded control | Standard ACPI EC, banked Super-I/O EC, indexed ports, raw port I/O, and model-specific register layouts | No generic WSGM EC surface; reuse access modules separately from device layouts and safety policy |
| Processor power | OEM WMI, custom ACPI methods/IOCTLs, AMD SMU-family paths, Intel MMIO/MSR paths, and vendor scenario modes | TDP belongs wholly to the plugin even though WSGM retains semantic power-limit controls |
| OEM WMI | Queries, event subscriptions, named methods, fixed buffers, nested byte packages, and vendor-specific method dialects | Inventory signatures and run only cataloged probes; method presence never authorizes invocation |
| Device IOCTL and ACPI | Vendor device interfaces, fixed IOCTL contracts, and evaluated ACPI methods | Treat each protocol as a reviewed module with exact lengths, identity gates, and recovery |
| HID control | Interrupt input, output reports, feature get/set, request/response exchanges, report-ID framing, CRC/checksum, apply/commit, and persistent profile commands | Model endpoint, protocol, layout, and persistence independently; never infer that a setter is volatile |
| Native libraries | OEM DLL exports and third-party native helpers with their own process, bitness, signer, and deployment requirements | Inventory without invoking unknown exports; package and audit dependencies per plugin |
| Serial devices | COM endpoints with device-specific baud, framing, request/response, and reconnect behavior | Provide capture/framing tools, not a generic runtime serial-control service |
| Fans and thermals | Direct duty, target RPM, firmware modes, one or multiple fans, curve tables, tachometer scaling, and full-speed flags | The plugin owns channel layout, safe takeover, readback, watchdog behavior, and firmware release |
| Lighting and charging | HID, WMI, EC, serial, persistent profile memory, one or many zones, battery thresholds, bypass, and conservation modes | Separate volatile from persistent operations and require exact firmware-specific restoration evidence |
| Controller and motion | Raw HID, XInput, DirectInput, SDL, WinRT/controller sensors, detachable endpoints, mode switching, re-enumeration, and vendor rumble | Generate independent source/sink modules and preserve topology generations across mode changes |
| OEM controls | F13-F24, Win/Ctrl/Alt/Shift chords, Win+G/Win+Tab, mouse buttons, WMI events, vendor HID events, and extra controller buttons | Correlate all sources; publish stable logical controls; keep action mapping in WSGM and device-specific suppression in the plugin |
| Lifecycle | Hotplug, suspend/resume, controller PID changes, OEM-tool conflicts, firmware state, and partial capability failure | Own and recover resources independently; shell-mode transitions must not recreate the device lifecycle |

The sweep rules out a single inheritance tree in which a device merely overrides a few addresses. It supports exact device definitions composed from reusable, independently evidenced modules.

## Locked architectural boundary

> WSGM defines what a device capability means. The device plugin completely owns how that capability is implemented on its hardware.

The HC sweep showed that the same user-facing capability may be implemented through unrelated mechanisms on different devices, or even on different firmware generations of the same family. TDP may use OEM WMI, a custom ACPI IOCTL, AMD SMU, Intel MMIO/MSR, or another vendor protocol. Fans, lighting, charging, controllers, and OEM buttons vary just as widely.

WSGM therefore does not provide a hardware-control implementation surface to plugins.

There is no WSGM-owned:

- AMD TDP service.
- Intel TDP service.
- Generic EC service.
- PawnIO service.
- Raw WMI execution API.
- Raw HID feature/output API.
- Arbitrary DeviceIoControl API.
- Raw port, MMIO, MSR, ACPI, SMBus, or serial proxy.
- Shared administrator-capable hardware broker available to community plugins.

Reusable transport or platform code may exist as versioned SDK libraries or implementation modules linked into a plugin. Those libraries execute inside and remain the responsibility of that plugin. They are not resident WSGM services and are not part of the runtime IPC contract.

### Responsibility split

| WSGM owns | Device plugin owns |
| --- | --- |
| Plugin discovery, package policy, selection, and host supervision | Exact model, board, revision, and firmware detection |
| Semantic capability schemas and versioned IPC | Every hardware transport, protocol, layout, and command sequence |
| First-party overlay controls | TDP and power implementation |
| Native Steam QAM projection | Fan modes, tables, targets, readback, and release |
| Desired semantic values and per-application profile selection | Lighting, charging, telemetry, and peripheral implementation |
| Capability freshness, command progress, and error presentation | Hardware ranges, validation, ordering, readback, and rollback |
| One selected plugin package per logical handheld | Per-resource hardware acquisition, conflicts, ownership, and partial degradation |
| DeviceHost process lifetime, timeout, restart, and quarantine | Hardware snapshots and recovery journal |
| HIDMaestro virtual targets | Physical controller acquisition, mode changes, and re-enumeration |
| HidHide transaction ownership | Exact physical-controller identities supplied to HidHide |
| Canonical controller input/output contracts | Raw input decoding, normalization, motion, and rumble encoding |
| WSGM UI input arbitration | Publishing logical OEM-button events and device-specific suppression |
| Allowlisted OEM actions | Mapping physical OEM sources to stable logical controls |
| RTSS, Steam CEF, and native QAM integration | Device-specific dependencies and their health |

### What plugin-owned TDP means

WSGM may receive a semantic capability such as:

```text
capability: power.primary-limit
current: 18 W
range: 8–30 W
step: 1 W
quality: verified
```

The overlay and QAM may request 20 W through that semantic capability. The plugin decides whether that means MSI WMI, an EC transaction, AMD SMU, Intel MMIO, or another mechanism.

WSGM validates IPC shape and UI consistency. The plugin performs the authoritative hardware validation again, applies the operation, reads it back where possible, reports the result quality, and owns rollback.

The semantic contract is necessary. A WSGM hardware backend is not.

### Physical-controller boundary

The physical and virtual sides remain deliberately separate:

1. The plugin acquires and normalizes the physical controller.
2. The plugin publishes canonical handheld input and stable physical-device identities.
3. WSGM owns the UI-input arbiter, HidHide transaction, HIDMaestro target, and target selection.
4. WSGM routes canonical virtual-target output to the plugin.
5. The plugin encodes and sends physical rumble, haptics, lighting, or other device output.

Plugins never call HIDMaestro directly and never own WSGM's Steam Input lease.

## Semantic capability contract

WSGM needs stable semantic schemas so the overlay, QAM, profiles, persistence, and diagnostics remain device-independent.

Initial standard capability families are:

- Sustained, slow, fast, and peak power limits.
- OEM performance or scenario mode.
- Fan mode, fan duty, target RPM, fan curve, and measured RPM.
- Charge limit, charge-protection mode, and bypass charging.
- Lighting power, brightness, zones, colors, effects, and speed.
- Hardware telemetry.
- Physical controller source.
- Motion source.
- Rumble or haptic sink.
- OEM logical controls.
- Device-specific toggles, ranges, choices, actions, and read-only values.

The generic toggle/range/choice/action schemas permit unusual features such as UMA allocation, a secondary-display brightness control, USB-C routing, touchpad pass-through, or an external-GPU mode without allowing plugin-supplied XAML, HTML, JavaScript, or arbitrary UI code.

### Capability descriptor

Each descriptor contains stable metadata:

- Stable capability and instance IDs.
- Semantic role.
- Display metadata selected from WSGM-owned schemas.
- Supported read, write, and action operations.
- Minimum, maximum, step, and unit where applicable.
- AC versus battery availability.
- Mutually exclusive modes.
- Volatile, device-persistent, or unknown persistence.
- Whether activation, re-enumeration, restart, or reboot is required.

Device-level display metadata may also select a reviewed WSGM-owned physical controller glyph profile by stable ID. The profile is presentation only: WSGM resolves it from its pinned catalog for Steam CEF and first-party surfaces. Plugins cannot supply CSS, JavaScript, XAML, SVG, URLs, or arbitrary artwork, and an unknown profile ID is ignored. Device Lab may recommend a glyph profile only from an exact known-device match; generated scaffolds leave it unset until the physical diagrams and OEM-button positions are visually verified.

The separately versioned live capability state contains:

- Availability and command-progress state.
- Observed or applied hardware value.
- State quality and observation time.
- Host and device generations.
- Structured unavailable or degraded reason.

WSGM's capability projection adds the authoritative requested/desired value, profile source, and UI command progress. A plugin may journal transient applied intent for recovery, but it does not own the persisted desired state.

The plugin is the authority for ranges and hardware safety. WSGM must not assume that a value accepted by a slider is safe merely because the plugin advertised it earlier; the plugin validates every command against current firmware and state.

### Truthful command and state model

Command completion distinguishes:

- Accepted.
- Applied but unverified.
- Applied and verified.
- Rejected.
- Timed out.
- Indeterminate.

Plugin-reported hardware state quality distinguishes:

- Unknown.
- Observed.
- Verified.
- Stale.
- Faulted.

WSGM may project `Requested` while a semantic command is pending, but that is not an observed hardware state.

A successful IPC reply is not automatically hardware readback. If the plugin host disconnects, its device generation changes, or its observation expires, WSGM marks the state stale and disables affected commands rather than displaying cached values as current.

## Plugin composition model

New device support must reuse components, not inherit an older monolithic device class.

Four layers are kept distinct:

| Layer | Responsibility | Examples |
| --- | --- | --- |
| Transport | Moving bytes or invoking the platform interface | HID, WMI, EC, IOCTL, serial, native DLL |
| Protocol | Framing, methods, commands, checksums, and responses | MSI named WMI blocks, Claw MCU frames, Zotac CRC |
| Layout | Addresses, offsets, masks, channels, zones, and axes | Fan-table fields, RGB profile base, OEM-button bits |
| Policy | Device-specific limits, persistence, ordering, and recovery | A2VM power limits, firmware gates, safe fan release |

A device may reuse an MSI WMI transport and Claw controller protocol while requiring a new fan layout and its own power policy. Reusing the transport must never import another model's limits, offsets, or firmware assumptions.

### Three reusable units

- **Plugin package:** deployment, publisher, dependencies, executable content, and process boundary.
- **Device definition:** exact identity/firmware gates and the composition selected for one model.
- **Implementation module:** reusable code for a transport, protocol, layout, policy, or capability.

Illustrative module IDs:

```text
MsiWmiPlatform@1
MsiClawMcu@2
MsiClawDInput@2
MsiClawA2VmPowerPolicy@1
AmdFamily19hPower@3
OneXPlayerSerialLighting@1
```

Modules are version-pinned dependencies of the generated plugin. WSGM core never sees their raw APIs.

## Runtime process and trust model

### Per-plugin host

Each selected plugin package runs in its own `WSGM.DeviceHost.exe` process.

**Privilege is decided per trust tier at spawn.** Hardware verification on the MSI Claw A2VM
(2026-08-27) showed the OEM WMI provider returns `WBEM_E_ACCESS_DENIED` from a medium-integrity
process, so an unconditionally unelevated host cannot serve power, fan, thermal, EC or battery
capabilities at all. WSGM already runs elevated and de-elevates what does not need privilege, so:
reviewed first-party packages inherit that elevation directly; signed-external, sideloaded and
developer packages are spawned de-elevated and simply do not receive privilege-dependent
capabilities. No broker, helper executable, or generic privileged channel is introduced.

The host:

- Loads exactly one plugin package.
- Negotiates the semantic lifecycle and capability protocol with WSGM.
- Owns plugin cancellation and disposal.
- Uses a kill-on-close job and bounded process resources.
- Receives no WSGM secrets or unrelated device handles.
- Uses deterministic DLL search paths.
- Exposes no generic execute, shell, file, WMI, HID, EC, or IOCTL command over IPC.

Process separation provides dependency and crash isolation. It is not described as a malware sandbox. A normal medium-integrity plugin can still exercise the rights of the user account.

### Trust tiers

| Tier | Runtime policy |
| --- | --- |
| WSGM-reviewed | Reviewed and built through the WSGM release process; eligible for full approved hardware support |
| Signed external | Explicit user installation and enablement; host runs at ordinary user integrity with no WSGM-provisioned elevation; any publisher-owned privileged component is independently installed and trusted by the user |
| Sideloaded community | Permanent unreviewed-code label; host runs at ordinary user integrity with no WSGM-provisioned elevation; any external privileged component remains independently installed and outside WSGM trust |
| Developer | Explicit Developer Mode and Device Lab; never auto-activated as a normal plugin |

For WSGM 2.0, WSGM never provisions elevation or installs a driver, service, task, helper, or dependency on behalf of an unreviewed plugin. Such plugins receive ordinary user rights only and may use only components the user installed independently. They cannot add operations to a WSGM-reviewed privileged profile.

### Privileged hardware

If a reviewed plugin requires privilege, it may include a separately signed, device-specific helper or rely on a separately audited production-signed driver.

That helper:

- Belongs to the plugin package, not to a generic WSGM hardware broker.
- Exposes only fixed, device-scoped operations.
- Independently validates board, firmware, ranges, lengths, rate, and current ownership.
- Accepts no arbitrary registers, buffers, WMI methods, IOCTLs, scripts, paths, or executables.
- Is installed in an administrator-protected location.
- Is hash- and signer-pinned by the reviewed package.
- Is safe even if another same-user process invokes one of its legal operations.

Community plugins needing privilege must supply and independently install their own properly signed component. WSGM does not turn a community manifest into administrator authority.

### Dependencies

Plugins declare dependencies; they do not install or repair them at runtime.

A missing OEM provider, DLL, service, driver, or signed helper makes the affected capability unavailable. The plugin must not silently:

- Copy an OEM provider DLL.
- Edit an ACPI/WMI provider registry path.
- Restart an ACPI device.
- Disable or kill an OEM service.
- Install a driver or certificate.
- Run an arbitrary installer.

WSGM setup or update may later install only dependencies from an audited component catalog with fixed version, hash, signer, license, ACL, and removal behavior. Plugin runtime never performs that installation.

## Lifecycle and ownership

The WSGM device lifecycle remains bound to the entire WSGM run.

The selected plugin host starts asynchronously when WSGM starts with Device Integration enabled and remains active across Desktop Mode, Game Mode, games, Steam restarts, and shell transitions. WSGM intentionally deactivates the device cycle only when WSGM exits or Device Integration is turned off in Settings. An unexpected host exit is a fault inside that same cycle and causes bounded restart, backoff, or quarantine; it is not a handoff or normal deactivation.

The plugin owns:

- Hardware activation and initial snapshots.
- Per-resource acquisition.
- Suspend and resume.
- Hotplug and re-enumeration.
- Hardware apply, verification, and rollback.
- Releasing firmware fan mode, controller takeover, lighting holds, and similar state.
- Its durable recovery journal.

WSGM owns:

- Plugin-host start, stop, timeout, restart, and crash-loop quarantine.
- Desired semantic state and per-application selection.
- Capability freshness and user-visible health.
- HidHide, HIDMaestro, virtual-target, and UI-input cleanup.

WSGM cannot restore hardware through an implementation it deliberately does not own. A modifying capability is retail-eligible only when its plugin has proven safe behavior after cancellation, disconnect, host crash, suspend, and ambiguous operation results.

Controller handoff is two-phase. WSGM neutralizes its virtual target but keeps the physical device hidden; the plugin stops acquisition, restores the original mode, and acknowledges the resulting topology; only then does WSGM remove its virtual target and HidHide entries. A bounded-timeout path honors the user's stop request but records an unverified handoff instead of claiming clean restoration.

Persistent operations are never blindly retried after an indeterminate result. Custom fan control requires autonomous firmware safety or a verified plugin-owned lease/watchdog that restores automatic control if the plugin disappears.

Ownership remains per resource. A controller conflict must not automatically disable fan, lighting, power, charge, or OEM-event capabilities.

## Known-implementation catalog

Device Lab consumes a versioned developer catalog describing existing implementation modules and the evidence required to reuse them. Normal WSGM runtime detection does not search this catalog.

Each catalog profile contains:

| Group | Required information |
| --- | --- |
| Identity | Stable module ID, version, kind, dependencies, and conflicts |
| Candidate matching | Required, excluded, optional, and weighted observations |
| Firmware | Exact versions, ranges, descriptor hashes, and unknown-firmware behavior |
| Endpoint roles | Controller input, MCU control, RGB, detachable sides, sensor, and other roles |
| Transport | WMI scope/methods, HID selector, serial framing, EC backend, or helper identity |
| Protocol | Frame/report length, command/response invariants, CRC/checksum, and timing |
| Layout | Addresses, offsets, masks, endian, channels, zones, and axes |
| Capabilities | Semantic capabilities the module may implement |
| Safety | Bounds, rates, environmental requirements, hazards, and persistence |
| Probes | Versioned compatibility recipes and expected evidence |
| Recovery | Snapshot, rollback, emergency action, and verification |
| Evidence | Captures, fixtures, devices, firmware, and confidence |
| Licensing | Fact, reference, code, dependency, and redistribution provenance |

Identity similarity nominates candidates. It never authorizes a protocol or write.

Hard constraints are evaluated before scoring. Wrong report length, excluded firmware, absent required WMI method, incompatible CPU family, mismatched descriptor hash, or missing endpoint rejects the affected module instead of merely lowering its score.

Candidate assessment keeps three independent values:

- Reuse rank.
- Evidence grade.
- Write eligibility.

A highly ranked candidate may remain read-only.

## Device Lab

### Product surfaces

The developer tooling consists of:

- **WSGM Device Lab:** guided graphical workflow for hardware owners and plugin developers.
- **`wsgm-device` CLI:** repeatable inventory, probing, capture, scaffold, validation, and CI commands.
- **WSGM Device Plugin SDK:** semantic contracts, host adapter, templates, analyzers, generator, fixtures, and TestKit.
- **Implementation catalog and modules:** optional version-pinned hardware libraries bundled into and owned by each plugin; never runtime services or host-injected raw APIs.
- **DeviceHost diagnostics:** versioned read-only observations and capture taps for a running production plugin.

The NativeAOT WSGM process never loads plugin projects, analyzers, generators, WMI libraries, or reflection-heavy tooling.

Device Lab does not own or restart the normal WSGM device lifecycle. When production DeviceHost already owns a resource, Device Lab asks the active plugin to open a bounded, read-only, plugin-owned diagnostic session; DeviceHost forwards only the resulting observations and does not call plugin activation or deactivation. A direct Device Lab trial requires explicit operator action, a distinct experiment lease, and an orderly per-resource release by the active plugin. Device Lab never receives a raw runtime transport through WSGM IPC, silently disables Device Integration, or races the active plugin.

Compatibility probes execute in separate disposable probe hosts. Their typed, profile-scoped probe interfaces never appear in production DeviceHost IPC.

### Two contributor experiences

**Hardware Owner mode** requires no SDK knowledge:

1. Select the newly detected handheld.
2. Review what the sweep will observe.
3. Run safe inventory and known compatibility checks.
4. Follow simple prompts for remaining labeled actions.
5. Review restoration and privacy status.
6. Export a sanitized capture for a developer.

**Plugin Developer mode** adds:

- Candidate-module comparison.
- Probe detail and evidence review.
- Endpoint inspection.
- Report and event analysis.
- Module composition.
- Project generation.
- Fixture replay.
- Live acceptance and packaging checks.

## Automated bring-up workflow

### Stage 0: preflight

Device Lab verifies:

- Explicit output or temporary directory.
- Current device-integration and resource ownership.
- AC/battery and thermal prerequisites.
- Conflicting OEM tools.
- Available event sources and access.
- Existing drivers, providers, DLLs, and helpers.
- Whether any proposed probe needs elevation or a reviewed helper.

No test or probe uses the user's live `%LOCALAPPDATA%\WSGM` data.

### Stage 1: automatic inventory

The sweep records:

- SMBIOS manufacturer, product, model, baseboard, and revision.
- BIOS, EC, controller/MCU, HID `bcdDevice`, and provider versions.
- CPU and GPU identities.
- Full PnP/container topology and arrival generations.
- USB/HID VID, PID, MI, usage, caps, and report lengths.
- WMI namespaces, classes, instances, events, method signatures, and qualifiers.
- COM devices and framing candidates.
- WinRT and controller sensor availability.
- XInput, DirectInput, SDL, and raw-HID views.
- Native DLL name, version, hash, signer, and exports without invoking unknown exports.
- Relevant processes, services, tasks, and current ownership conflicts.

Unique identifiers are retained only in the private working capture and redacted from shareable output.

### Stage 2: offline candidate matching

The normalized inventory is matched independently against known transport, protocol, layout, policy, and capability modules.

The tool:

1. Removes every hard-incompatible module.
2. Explains every rejection.
3. Ranks the remaining candidates.
4. Shows what each candidate would reuse.
5. Lists device-specific values that must not be inherited.
6. Identifies the next safe probe that would distinguish ambiguous candidates.

No device handles are opened during offline matching.

### Stage 3: passive observation

Device Lab may ask the maintainer to press buttons, move axes, rotate the device, attach/detach controllers, or change one setting in the official OEM utility.

One QPC-aligned timeline may contain:

- PnP arrival/removal.
- Raw HID input.
- Raw Input keyboard/mouse.
- Low-level hook observation.
- WMI device events and WMI Activity.
- XInput, DirectInput, and SDL state.
- WinRT/controller/serial sensors.
- Plugin operations and operator markers.
- Optional telemetry and readback.

Passive correlation can strengthen a candidate. It does not prove causality by itself.

### Stage 4: known read probes

Candidate modules expose named, versioned read probes. Device Lab invokes only probes already reviewed as safe for the matched family and endpoint.

Only WSGM-reviewed, hash-pinned catalog probe code may execute during the automatic sweep. Signed-external, sideloaded, and developer-module probes require an explicit Developer Mode action even when their metadata labels them read-only.

Examples include:

- Provider or protocol version query.
- WMI status/current-value method.
- Known HID feature read.
- Known allowlisted EC read.
- Controller mode/profile read.
- Fan RPM or charge-state read.
- Native-library version/export inspection.

The tool never activates an older plugin's normal lifecycle merely to see whether it works. Activation may contain writes. It runs the candidate module's dedicated compatibility probe entry point in a disposable test host.

A nonempty response is insufficient. The response must satisfy its structural invariants, expected length, status, range, timing, and cross-checks.

### Stage 5: bounded compatibility trials

Write-capable trials are never part of the unattended sweep. The maintainer starts one capability-specific trial after reviewing its exact effect and recovery.

Examples:

- Short low-amplitude rumble followed by guaranteed zero output.
- One-step temporary power change under the candidate policy, followed by readback and exact pair restore.
- One fan at current-or-higher safe duty, with RPM verification and restoration of firmware mode.
- One low-brightness RGB zone only when the matched profile proves the command is volatile.
- Controller-mode change with continuation across PnP re-enumeration and restoration of the original mode/PID.

Each trial:

- Acquires only the affected resource.
- Rechecks exact board and firmware.
- Records original state durably.
- Has bounded actions, rate, retries, timeout, and cooldown.
- Defines an independent observation or readback.
- Defines rollback and an emergency action.
- Verifies restoration.

One trial never combines unrelated capabilities.

### Stage 6: assessment and composition

Probe results record independent dimensions:

```text
execution:
  completed | timeout | access-denied | conflict | disconnected |
  prerequisite-missing | cancelled

observation:
  match | mismatch | no-signal | unstable | topology-changed

mutation:
  none | applied-verified | applied-unverified | not-applied

cleanup:
  not-required | restored-verified | restore-unverified | restore-failed
```

The derived compatibility verdict is:

- Compatible.
- Incompatible.
- Inconclusive.
- Blocked.
- Quarantined.

Any failed or unverified restoration quarantines that resource and prevents generation of a write-capable implementation.

### Stage 7: project generation

The generator creates an exact new device definition and composes version-pinned implementation modules that the evidence supports.

The output status is `Scaffolded`, not `Supported`.

The generated project must compile and pass its offline fixtures immediately. Unverified capabilities are omitted or expose a structured unavailable reason; they never appear as placeholder setters.

## Generated plugin project

Illustrative output:

```text
WSGM.Device.Msi.NewModel/
├─ plugin.wsgm.json
├─ evidence.lock.json
├─ README.md
├─ bring-up-report.md
├─ src/
│  ├─ NewModelPlugin.cs
│  ├─ DeviceDefinition.cs
│  ├─ Capabilities/
│  ├─ Protocol/
│  └─ Generated/
│     ├─ DeviceFingerprint.g.cs
│     ├─ EndpointCatalog.g.cs
│     ├─ ModuleComposition.g.cs
│     └─ EvidenceIds.g.cs
└─ tests/
   ├─ Fixtures/
   ├─ Generated/
   │  └─ CaptureReplayTests.g.cs
   └─ HardwareAcceptance.cs
```

### Automatically populated

- Exact detector and negative detection cases.
- Resource and endpoint graph binding.
- Plugin manifest, declared publisher metadata, dependencies, and risk declarations.
- References to selected implementation modules.
- Semantic capability registrations that have sufficient evidence.
- Lifecycle and resource-lease skeleton.
- Recovery-journal fields required by selected modules.
- Captured controller/button/sensor parsing where the mapping is verified.
- Golden fixtures and replay tests.
- Unknown-firmware rejection tests.
- Accepted, rejected, and unresolved candidate report.
- Evidence and licensing provenance.

Generated projects begin in `Scaffolded`/Developer state. Installation and review policy assigns package trust externally; generation never grants it.

### Never populated merely from similarity

- Another model's power limits.
- Fan table or RPM conversion.
- RGB profile offsets or persistent commands.
- Charge-policy writes.
- EEPROM, ROM, or UEFI synchronization.
- Unknown IOCTL, EC, MMIO, MSR, or ACPI operations.
- Rollback that has not been tested on the target.
- A write-capable capability whose restoration is unverified.

### Regeneration

Generated `.g.cs` files and developer-owned files remain separate.

Re-running the sweep after a firmware update may:

- Update inventory and evidence locks.
- Re-evaluate module compatibility.
- Add new generated fixtures.
- Mark a previously compatible module inconclusive.
- Disable a capability whose firmware gate no longer matches.

It must never overwrite handwritten code or silently accept changed golden output. Fixture updates require an explicit semantic diff and acceptance.

## MSI family example

For a newly attached MSI handheld, Device Lab should:

1. Confirm MSI SMBIOS identity, exact board, CPU family, BIOS, EC, and controller firmware.
2. Enumerate `MSI_ACPI`, `MSI_Event`, named WMI methods, provider version, and buffer shapes.
3. Inventory controller and MCU HID endpoints, report lengths, usage, PID, and `bcdDevice`.
4. Rank known MSI WMI transports, Claw power protocols, fan layouts, MCU protocols, controller codecs, RGB layouts, sensor sources, and OEM-event sources independently.
5. Run the safest known MSI version/status and current-value probes.
6. Present the closest existing device definitions and every hard mismatch.
7. Guide the maintainer through only the unresolved capability trials.
8. Generate a new exact model definition composed from the modules that passed.

For the A2VM reference:

| Candidate | Expected initial decision |
| --- | --- |
| MSI named-method WMI transport | Reusable after provider and 32-byte response validation |
| MSI Claw WMI power protocol | Candidate until PL1/PL2 reads validate |
| `MS-1T52` power policy | Exact candidate with A2VM-specific limits |
| A1M power policy | Hard reject; limits must not cross the board boundary |
| MSI 64-byte MCU protocol | Candidate after endpoint and response validation |
| XClaw input source | Candidate for the XInput PID |
| DClaw input/rumble source | Candidate for the DirectInput PID |
| MSI fan layout | Inconclusive until table length, channels, flags, and RPM are verified |
| MSI RGB layout | Writes denied until exact firmware profile and physical zone order are verified |
| MSI WMI OEM-event source | Candidate after target event correlation |
| Win+G suppressor | Exact-device policy only; never the OEM action source |

If `MSI_Event` is absent, the result is prerequisite missing. Device Lab does not copy a provider DLL, edit `WmiAcpi`, or restart `ACPI\PNP0C14` to manufacture it.

## Capture and evidence bundle

Private working sessions are stored separately from sanitized, shareable `.wsgmcap` bundles.

A deterministic shareable bundle contains:

```text
manifest.json
recipe.json
inventory.json
streams/*.ndjson
analysis/*.ndjson
claims.json
blobs/*
redaction.json
hashes.sha256
```

The captured `recipe.json` is inert evidence of the observe-only steps that ran. Imported captures, recipes, manifests, plugins, or evidence locks can never authorize or supply a hardware mutation. The trial runner accepts only a locally installed, WSGM-reviewed catalog trial ID and exact hash.

Every event carries:

- Source and recipe-step IDs.
- Per-source and global sequence numbers.
- QPC receipt time and source timestamp where available.
- Clock segment and device generation.
- Exact payload length and bytes where permitted.
- Loss, discontinuity, timeout, and access status.

Derived interpretations never replace raw evidence.

### Evidence ledger

Each claim records:

- Stable claim ID.
- Device, board, revision, and firmware scope.
- Transport and endpoint.
- Raw selector, offset, mask, width, endian, scale, unit, and range.
- Proposed semantic meaning.
- Supporting observations and counterexamples.
- Repetition and restoration results.
- Analyzer and version.
- Source and licensing provenance.
- Known limitations.
- Superseded claim where applicable.

Claim states are:

- Candidate.
- Correlated.
- Corroborated.
- Hardware verified.
- Retail approved.
- Rejected.

`evidence.lock.json` pins the accepted claims and module versions used by generated code. A protocol constant cannot change silently without an evidence diff.

### Privacy

Shareable output redacts by default:

- User and computer names.
- SIDs, profile paths, and command lines.
- Serial numbers and stable instance/container IDs.
- MAC and Bluetooth addresses.
- SSIDs, BSSIDs, IP addresses, Steam IDs, volume IDs, and window titles.
- Unrelated keyboard text or input.

Opaque ETL or pcapng data that cannot be safely rewritten is excluded by default and marked quarantined.

Nothing uploads automatically.

## Analysis tools for unknown gaps

When known modules do not cover a capability, Device Lab provides:

- PnP/container and endpoint explorer.
- QPC-aligned multi-lane timeline.
- HID report matrix and bit/byte differential view.
- WMI schema, method, qualifier, event, and activity browser.
- Serial framing and timing view.
- Controller comparison across raw HID, XInput, DirectInput, and SDL.
- IMU six-face orientation, rate, jitter, scale, sign, and axis analysis.
- Baseline/action/release comparison.
- Integer, signed, endian, mask, scale, and offset candidate decoders.
- Counter, noise, checksum, and CRC hypothesis analysis.
- Cross-device and cross-firmware capture comparison.

Selecting a derived value always links back to its supporting raw observations.

### Honest platform limitations

- User-mode HID APIs do not passively show another process's output or feature writes.
- USB FullData ETW is bounded, lossy, USB-only, elevated, and privacy-sensitive.
- WMI Activity can identify operations but does not promise method arguments or result payloads.
- Kernel/process ETW is not a universal private-IOCTL transcript.
- Raw Input can identify a keyboard; a low-level suppression hook cannot.
- Supported desktop APIs do not provide generic ACPI, EC, SMBus, or I2C access.
- Before/after snapshots are not atomic and may contain counters, hysteresis, and delayed work.
- Timing correlation produces a candidate, not proof of causality.

The tool labels these limitations explicitly instead of turning weak evidence into a confident mapping.

## Probe safety policy

### Automatic

- Identity and topology enumeration.
- Descriptor/capability inventory.
- Passive event and input capture.
- Existing dependency/version inventory.
- Offline candidate matching.
- Known profile reads explicitly classified safe for the matched family.

### Explicit maintainer action

- One volatile power change.
- One fan takeover/release test.
- One bounded rumble pulse.
- One volatile RGB change.
- Controller mode switching.
- Keyboard suppression simulation.
- Any operation requiring exclusive ownership or elevation.

### Manual-only or out of scope

- EEPROM, ROM, or UEFI writes.
- Firmware flashing.
- Provider deployment or registry repair.
- Driver installation or restart as a probe.
- Charge-policy persistence.
- Blind EC/SMBus/I2C scanning.
- Unknown IOCTL, HID output, ACPI, MMIO, MSR, physical-memory, or raw-port probing.
- Public test certificates or test-signing mode.

Even a getter is not assumed safe merely because it reads. Known feature reports and register reads remain profile-scoped, rate-limited, and timeout-bounded.

## CLI workflow

```text
wsgm-device doctor
wsgm-device inventory
wsgm-device candidates
wsgm-device probe known --read-only
wsgm-device probe run <probe-id>
wsgm-device capture run <recipe>
wsgm-device inspect <capture>
wsgm-device diff <capture-a> <capture-b>
wsgm-device correlate <capture>
wsgm-device plugin scaffold --from <capture>
wsgm-device fixture extract <capture>
wsgm-device validate offline <plugin>
wsgm-device validate hardware <plugin>
wsgm-device pack <plugin>
```

`inventory`, `candidates`, `capture run`, `inspect`, `diff`, `correlate`, `scaffold`, fixture extraction, offline validation, and packaging cannot mutate hardware. `capture run` records an observe-only recipe; mutation always uses one named `probe run` compatibility trial.

`probe run` is the single mutation path and refuses unattended execution. It accepts only a locally installed, WSGM-reviewed catalog trial ID and hash, then requires a local interactive maintainer to review and authorize that one trial's exact identity gates, actions, maximum writes, effect, experiment lease, rollback, and emergency behavior. Authorization expires when the trial, device generation, module version, or preflight changes. Capture files, plugin packages, and imported recipes are never executable authority. There is no `--yes`, CI, recipe nesting, or bulk “test all” bypass.

`validate hardware` accepts only the generated target package under development plus a reviewed acceptance manifest. It runs inventory, passive/read checks, fixture comparison, and acceptance assessment without activating any older candidate plugin. When mutation evidence is missing, it emits the required named compatibility trials and remains incomplete; it cannot invoke those trials itself.

A later full-lifecycle test runs outside Device Lab through explicit WSGM Developer Mode. It is available only after every possible activation-time hardware mutation is already hardware verified through a named `probe run` trial. WSGM shows the target package, risk declarations, and verified activation operations before the maintainer enables that plugin lifecycle. This is neither candidate reuse testing nor blanket mutation authority for an acceptance manifest.

`validate` and `pack` do not confer package trust, privileged authorization, hardware verification, or retail support.

## Fixture and validation model

Fixtures are plain, reviewable directories derived from sanitized captures. Replay is simulator-only and can never issue hardware writes.

Required fixture families include:

- Positive and negative device detection.
- Firmware and endpoint variants.
- Neutral, press/release, axis, motion, and OEM-event sequences.
- Semantic command to expected plugin-owned transport intent.
- Capability snapshots and unavailable reasons.
- Truncated, malformed, delayed, and out-of-order responses.
- Timeout, event loss, access denial, resource conflict, and disconnect.
- PnP re-enumeration.
- Suspend and resume.
- Full WSGM process lifecycle.
- Controller-management disable without stopping other device capabilities.
- Hardware rollback and ambiguous-result reconciliation.

Validation proceeds through:

1. Schema, manifest, evidence, and provenance lint.
2. Deterministic candidate matching and scaffold generation.
3. Clean compile.
4. Semantic IPC and NativeAOT boundary validation.
5. Offline fixture replay.
6. Lifecycle and injected-fault tests.
7. Explicit on-device acceptance.
8. Package, dependency, license, signer, and trust review.
9. Retail approval.

Passing offline replay proves deterministic software behavior against captured evidence. It does not prove that the hardware protocol is correct.

## Implementation sequence

The plan layers align as follows:

| Parent 2.0 phase | Device Lab work | Claw reference work |
| --- | --- | --- |
| Phase 0 contracts and experiments | D0-D3 contracts, inventory, first reviewed read modules, scaffold, and bounded trial runner; D4 analysis where M0 exposes unknown gaps | M0 read-only characterization, separately authorized trials, and scaffold regeneration |
| Phase 1 device integration | D4 analysis support as new runtime gaps appear | M1, M2, M4, and the plugin-owned physical-controller portion of M3 |
| Phase 2 controller integration | Fixture replay and hardware acceptance using D2-D4 tooling | WSGM-owned M3 HIDMaestro, HidHide, output routing, UI input, fallback, and end-to-end controller acceptance |
| Release hardening | D5 compatibility, packaging, and contributor workflow | M5 failure testing, dependency audit, and retail approval |

### D0: contracts and ownership

- Replace core-owned hardware backends with semantic capability contracts.
- Define package, host, trust, state-quality, and command-result contracts.
- Define device definition and implementation-module boundaries.
- Define the known-implementation catalog schema.
- Define private capture, sanitized export, fixture, evidence, and scaffold schemas.

### D1: read-only inventory and candidate engine

- Build machine/PnP/HID/WMI/controller/sensor inventory.
- Implement hard constraint filtering and explained candidate ranking.
- Implement reviewed, hash-pinned read-only probe hosting.
- Add the MSI family as the first catalog and bootstrap its safe inventory/read-probe modules before the generator references them.
- Produce the first A2VM baseline capture and fixtures.

### D2: deterministic scaffold

- Generate a clean plugin project, exact detector, module composition, manifest, evidence lock, and tests.
- Keep generated and developer-owned code separate.
- Compile and replay generated projects.
- Make unknown firmware and incomplete capabilities fail closed.

### D3: bounded compatibility trials

- Add transactional trial runner and recovery journal.
- Add capability-specific power, fan, rumble, lighting, and mode-switch trials.
- Add topology continuation and restoration verification.
- Keep persistent operations outside automatic trials.

### D4: analysis workbench

- Add correlated timelines and protocol analysis for unknown gaps.
- Add privacy-reviewed export and LLM-friendly summaries.
- Add cross-firmware/device comparison.

### D5: packaging and community workflow

- Freeze SDK, catalog, capture, and scaffold compatibility policy.
- Add deterministic package validation.
- Document source submission, hardware evidence, review, and retail-promotion workflow.
- Stabilize and publish the contributor templates produced during D0–D4.

## Acceptance criteria

The design is successful when:

- A newly connected handheld receives a complete safe inventory without hardware mutation.
- The tool finds and explains reusable modules from existing related devices.
- Hard mismatches are rejected before ranking.
- No similarity score grants write eligibility.
- Known modules are tested through dedicated probe entry points, never normal plugin activation.
- One failed resource probe does not invalidate unrelated capabilities.
- Every mutation is explicit, bounded, independently observed, and restored.
- Unknown firmware never selects the nearest persistent or memory layout.
- The generated project compiles and passes deterministic offline fixtures.
- Generated code references verified modules without importing another model's limits.
- Unverified features are unavailable rather than partially exposed.
- Every constant and module selection traces to evidence and provenance.
- The plugin owns all hardware implementation and cleanup.
- WSGM core remains free of device-specific transports and privileged hardware APIs.
- Desktop/Game Mode transitions never restart the production device lifecycle.
- Validation and packaging never masquerade as hardware or trust approval.

## Explicit non-goals

- An automatic reverse-engineering oracle.
- A generic EC register scanner.
- A universal hardware scripting language.
- Activating every older plugin until one appears to work.
- Generating writes from a single correlation.
- Treating a compiling scaffold as supported hardware.
- Letting plugins supply UI code.
- Letting community plugins borrow WSGM elevation.
- Installing arbitrary plugin dependencies.
- Replacing HIDMaestro with plugin-owned virtual controllers.
- General controller or gyro remapping.
- Cloud telemetry, automatic upload, or remote hardware control.

## Licensing and provenance

The known catalog distinguishes protocol facts, behavioral references, copied code, dependencies, binaries, and independently captured evidence.

Handheld Companion is treated as a behavioral and protocol reference at the audited revision. WSGM does not copy HC implementations, packet builders, or structured register tables without a deliberate licensing decision.

Every generated project retains:

- Catalog and module versions.
- Evidence hashes.
- Source and license provenance.
- Tested devices and firmware.
- Required third-party notices.
- Native dependency hashes and expected signers.

Proprietary OEM DLLs, helpers, providers, or drivers are never redistributed without established rights.

If a generated device definition selects a profile from Handheld Controller Glyphs, the evidence bundle records the exact WSGM catalog profile, upstream revision, asset hashes, and visual-verification status. That catalog is a WSGM dependency with its own attribution and update review; it is not copied into each plugin package.

## References

- WSGM handheld controller glyph integration: [controller-glyph-integration.md](./controller-glyph-integration.md)
- Handheld Companion audited revision: https://github.com/Valkirie/HandheldCompanion/tree/5c94abca83f8711ff5620906871b31a41c76bf05
- Microsoft HID APIs: https://learn.microsoft.com/windows-hardware/drivers/hid/hid-api
- Microsoft HID report guidance: https://learn.microsoft.com/windows-hardware/drivers/hid/obtaining-hid-reports
- Microsoft Raw Input: https://learn.microsoft.com/windows/win32/inputdev/about-raw-input
- Microsoft low-level keyboard hook: https://learn.microsoft.com/windows/win32/winmsg/lowlevelkeyboardproc
- Microsoft USB ETW capture: https://learn.microsoft.com/windows-hardware/drivers/usbcon/how-to-capture-a-usb-event-trace
- Microsoft WMI Activity tracing: https://learn.microsoft.com/windows/win32/wmisdk/tracing-wmi-activity
- Microsoft ACPI method evaluation: https://learn.microsoft.com/windows-hardware/drivers/acpi/evaluating-acpi-control-methods
- Microsoft SPB access model: https://learn.microsoft.com/windows-hardware/drivers/spb/spb-peripheral-device-drivers
