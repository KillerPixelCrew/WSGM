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

Never run live reads, capture, or `test hardware` merely because this skill activated. Get explicit
maintainer direction for the live scenario. `test plugin` executes the selected package with Device
Lab's authority, so use it only for trusted task code after reviewing the plugin constructor and
`DetectAsync` for side effects. `test hardware` is the sole Device Lab mutation door and must remain
hostile to automation.

## Follow the evidence funnel

1. Collect a private inventory, then make a separate `--shareable` projection if evidence will leave
   the machine.
2. Establish exact immutable identity: manufacturer, baseboard/SKU, firmware/provider, USB
   VID/PID/release and usage tuples. Marketing names and current paths are insufficient.
3. Analyze inventory and existing captures offline. Run only a compiled, reviewed, exact-match read
   probe when an observation cannot answer the question.
4. Observe one named physical action at a time across plausible channels. Align timestamps, report
   loss, and device generations; correlation produces candidates, not causality.
5. Encode the result in a typed plugin parser/service and hardware-free fixtures. Do not leave the
   discovery procedure as arbitrary runtime scripting.
6. Validate the package statically offline. Treat load/detect as a deliberate trusted-code boundary,
   not offline analysis. Use one explicitly selected attended workflow only after the identity,
   bounds, expected effect, readback, rollback, and cleanup path are explicit. `haptic-sweep` is a
   bounded multi-write calibration workflow, not a single hardware action.
7. Record observed facts separately from inference and from remaining live validation. Remove
   temporary raw-stream logging once the finding is captured in code, tests, and documentation.

## Probe without guessing

- Add a closed, compiled Device Lab profile for a new getter. Current candidate matching and
  compiled read probes cover the known MSI Claw fingerprint; a new handheld has no runnable profile
  until one is added in source. Never accept a WMI method, EC address, report id, output bytes, or
  script supplied by inventory/capture input.
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

Keep exact device-specific facts in that plugin's plan, source, tests, and provenance; keep the
generic discovery method here. Run Device Lab and plugin suites plus the focused WSGM protocol tests
without hardware first. Report which identity, parsing, lifecycle, restoration, and packaging facts
are proven offline and list every attended device matrix still outstanding.
