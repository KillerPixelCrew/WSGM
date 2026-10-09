---
name: wsgm-device-lab
description:
  Investigate and bring up handheld hardware for WSGM with Device Lab and reviewed developer probes,
  including exact identity, OEM buttons, raw HID/controller mapping, Steam-to-physical rumble and
  haptics, gyro and accelerometer discovery, captures, correlation, fixtures, and attended tests.
  Use for hardware evidence work; use wsgm-device-sdk for routine contract or host implementation.
---

# WSGM Device Lab

Turn observations into a typed, repeatable device contract. Device Lab is an evidence and validation
tool, not a general EC/WMI/HID poking shell.

## Begin with the safety class

1. Resolve the WSGM root, read the applicable `AGENTS.md`, and inspect the superproject plus nested
   submodule status. Preserve unrelated work and all exact dependency pins.
2. Read [references/safety-and-workflow.md](references/safety-and-workflow.md) before invoking
   `wsgm-device` or a developer probe. Its command classes are load-bearing.
3. Read [references/oem-and-controller.md](references/oem-and-controller.md) for buttons, controller
   modes, HID report mapping, and topology continuation.
4. Read [references/haptics-and-motion.md](references/haptics-and-motion.md) for Steam's finer
   feedback protocol, physical motor calibration, legacy sensors, axis transforms, and gyro bias.

Never run live reads, capture, or wizard hardware stages merely because this skill activated. Get
explicit maintainer direction for the live scenario. Hardware writes are confined to the attended
tester wizard and authenticated worker checkpoints. Retired package validation, packing and
plugin-test commands are removed.

## Route evidence work through existing components

| Need                                         | Existing path                                                                                                       |
| -------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| Explain or review a returned report          | Offline `report`, `review`, `promote` and `Reports/`; inspect evidence before changing curated facts.               |
| Inventory or match a handheld                | `Inventory/`, `Knowledge/DeviceKnowledgeMatcher.cs` and curated/extracted records.                                  |
| Read a known exact getter                    | Closed profiles in `Probes/` and their authenticated worker; no user-supplied register/method language.             |
| Observe raw buttons or motion                | Wizard stages over `Capture/Live/LabInputCapture*` and `LabMotionRecorder*`; capture broadly, attribute afterwards. |
| Bounded writes, streaming output and restore | Existing worker service interfaces/checkpoints, `LabPowerRecovery` and `LabMachineState`.                           |
| Turn evidence into a source contribution     | `Scaffolding/` and the [Device SDK skill](../wsgm-device-sdk/SKILL.md).                                             |

Keep CLI and GUI actions on the shared application services. Reuse the established inventory,
knowledge, redaction, worker, output-path and durable-file components instead of adding parallel
pipelines. Add one narrowly scoped collector or typed transport operation where needed; avoid new
generic brokers, guessed protocol engines and duplicate lifecycle/recovery frameworks.

Device Lab owns evidence collection and attended validation. LibHandheld owns runtime protocols and
model facts. WSGM owns profiles, user actions and presentation. Record a finding in the package's
model/provenance and fixtures, then document its runtime boundary; do not leave an exploratory lab
procedure as production hardware policy. Keep public XML contracts, README command examples and
these workflow references aligned with code. Inspect the matching fixture and refusal source tests,
while keeping execution under the root manual-first validation policy.

## Follow the evidence funnel

1. Collect a private inventory, then make a separate `--shareable` projection if evidence will leave
   the machine.
2. Establish exact immutable identity: manufacturer, baseboard/SKU, firmware/provider, USB
   VID/PID/release and usage tuples. Marketing names and current paths are insufficient.
3. Analyze inventory and existing captures offline. Run only a compiled, reviewed, exact-match read
   probe when an observation cannot answer the question.
4. Observe one named physical action at a time across plausible channels. Align timestamps, report
   loss, and device generations; correlation produces candidates, not causality.
5. Encode the result in a typed family parser/service and hardware-free fixtures. Do not leave the
   discovery procedure as arbitrary runtime scripting.
6. Review the source contribution and exact identity offline. Treat native acquisition as a
   live-code boundary, not offline analysis. Use one explicitly selected attended workflow only
   after the identity, bounds, expected effect, available readback and cleanup path are explicit.
   Production plugin writes can succeed without readback; the lab's stricter verification result
   must remain honest. `haptic-sweep` is a bounded multi-write calibration workflow, not a single
   hardware action.
7. Record observed facts separately from inference and from remaining live validation. Remove
   temporary raw-stream logging once the finding is captured in code, tests, and documentation.

## Probe without guessing

- Add a closed, compiled Device Lab profile for a new getter. Only the MSI Claw has one today, so
  another handheld has no runnable probe until one is added in source. Never accept a WMI method, EC
  address, report id, output bytes, or script supplied by inventory/capture input.
- Capture a neutral controller baseline, then press or move one control at a time through full
  travel and release. Prove report id/length, bit/byte, center, range, signedness, direction,
  diagonals, rollover, first-report corruption, and disconnect behavior.
- Observe OEM controls through WMI events, Raw Input, HID, controller state, and process/service
  effects. A low-level keyboard hook cannot name its source and is secondary suppression evidence,
  never the primary button source.
- Separate target-protocol intent from motor physics. Decode Steam's rich events in WSGM; declare
  the physical plugin's supported channels, floor, pulse, and rate from measurement.
- Enumerate WinRT, legacy Sensor API, and lower HID sensor collections. Verify exact fields, units,
  freshness/counter, cadence, and basis before mapping axes exactly once.

## Refuse unsafe shortcuts

Do not blind-scan neighboring EC registers, brute-force feature/output reports, disable a device or
driver, kill another manager, rewrite firmware profile memory, run imported recipes as code,
automate hardware confirmation, retain unbounded raw buffers, or trace at controller/sensor cadence.
A nonempty response or close timestamp is never proof that a command is safe or that an event
belongs to the pressed control.

## Finish with reproducible evidence

Keep exact device-specific facts in that device's plan, source, tests and provenance. Keep the
generic discovery method here. Write hardware-free fixtures and tests with the change, and build
before the maintainer's manual test. Run the Device Lab, plugin and focused WSGM protocol suites
after that test, as the root validation policy requires. Report which identity, parsing, lifecycle,
restoration and packaging facts are proven offline. List every attended device matrix that is still
outstanding.
