# Acer Predator Atlas 8 handoff

Research updated on 2026-10-27 using the supplied driver packages and Predator Sense 1.0.1563.0. All
investigation was offline. No vendor program was executed, device opened, service
installed/connected, hardware command sent or deployment run.

## Delivered implementation

LibHandheld commit `c83b75e`, integrated by WSGM `99961e53`, adds exact Acer `Predator PA08-I51` /
PTL `F900_PTG` detection, standard `0502:F901` XInput controller/rumble, raw Guide/QAM/M1/M2 input,
stock glyphs, lifecycle release handling and hardware-free fixtures. HardwareVerified remains false.
Power, fan, charging, RGB and motion controls have not been implemented for this model.

Release library and test-assembly compilation passed with zero warnings/errors. Thirty glyph assets
and their references were checked; packaging scripts parsed; changed Markdown/JSON was formatted.
Tests were not executed under the manual-first policy. Hardware, visible UI and installation remain
unverified. The local build log and artifact identity are retained in the research directory below.

## Recorded findings

- [Family provenance](../external/libhandheld/src/LibHandheld/Families/Acer/PROVENANCE.md) records
  InputPlumber evidence and exact input acceptance limits.
- [Power/fan protocol](../external/libhandheld/src/LibHandheld/Families/Acer/POWER_FAN_PROTOCOL.md)
  consolidates separate EC HID power and WMI fan transports, commands, field offsets,
  acknowledgements, mode/limit values, getter decoding, fan discovery and recovery constraints.
- [Predator Sense findings](../external/libhandheld/src/LibHandheld/Families/Acer/research/predator-sense-findings.md)
  preserve binary identities, controller/motion/RGB/calibration commands, lighting and
  native-service leads.
- [Managed power/fan callers](../external/libhandheld/src/LibHandheld/Families/Acer/research/power-fan-managed.md)
  preserve desktop enums, watt units, validation, service messages and fan controls.
- [Native agent and fan getters](../external/libhandheld/src/LibHandheld/Families/Acer/research/power-fan-agent-native.md)
  preserve exact function addresses, WMI bit packing, result handling, registry observations and
  completed fan-count/EC-health traces. The follow-up corrects the earlier global-mode inference:
  without its registry cache, the OEM getter reconstructs Custom/Auto, not Max.
- [Controller profile and motion findings](../external/libhandheld/src/LibHandheld/Families/Acer/research/controller-profile-findings.md)
  record persistent firmware gyro mapping versus raw upload, actual RAM-profile command `0x21`,
  volatile RGB previews, calibration lifecycle and the OEM queue's uncorrelated acknowledgements.
- [Supplied driver packages](../external/libhandheld/src/LibHandheld/Families/Acer/research/driver-package-findings.md)
  record exact archive hashes and INF matches for Acer's base/enabling services, the Acer-specific
  Intel sensor-hub firmware extension, Intel HID events and platform telemetry/provisioning.

## Local evidence and tools

The supplied package is under the maintainer's Downloads directory:
`Predator Sense_Acer_1.0.1563.0_W11x64_A/PS_HH_RC7_1.0.1563.0`. Binary digests and exact native
addresses are in the linked reports. No proprietary binaries, decompiled source, disassembly dumps
or extracted package payloads are committed.

Local, ignored `artifacts/acer-predator-research/` retains those raw artifacts, `power-managed/`,
`gamepad-decompiled/`, `rea-jobs/` request/result JSON and pseudocode, the earlier input build log,
`validation.json` and `power-fan-evidence.json`. Paths in copied research reports such as
`power-managed/` and `findings.md` refer to this original local evidence directory.

REA 6.8.0 ran through npm with Node 24.22.1. Ghidra 12.1.4 is extracted under
`artifacts/acer-predator-research/tools/ghidra_12.1.4_PUBLIC`; Microsoft OpenJDK 25 was installed
with winget. Ghidra 12.2 was also downloaded/verified, but REA 6.8.0 rejects that release line.
GitHub release metadata/digests and install logs are retained locally. No permanent agent MCP
registration was changed. The research MCP session was closed and its helper exited.

`rea_session.py` is a local reproducibility helper, not application code. Before reusing it, clear
only its own `rea-jobs/stop` marker and use new request filenames, since completed results are
retained. Do not infer a live Ghidra session from those saved results.

The follow-up used the connected REA MCP 6.8.0 with Ghidra 12.1.4 and managed PE/CLI/CIL inspection,
plus ILSpy 11 for readable managed reconstruction. The supplied ZIP SHA-256 is
`7bd158621b6e0135a046ecddb3df9e8b6fca0cc13b331046a4fa565a09784263`; both native service binaries and
AcerGamePad.Core.dll match the earlier research exactly. Ignored
`artifacts/acer-atlas8-rea-20261027/` retains the new archive graph, managed records, native
Evidence bundles/snapshots, driver inventories and detailed findings. The native sessions were
closed. Committed reports contain protocol facts and provenance, not vendor binaries or decompiled
source.

## Next work

Confirm the unit's live fan topology and capability replies, then finish mixed-fan recovery and
restoration sequencing. Static fan discovery is decoded: INI overrides take precedence; otherwise EC
health capabilities, with a legacy WMI fallback, supply the topology mask. Preserve raw per-fan
behavior/speed and known/unknown state, the full manual power profile and original active mode.
Avoid the vendor's unsafe continuation after failed prerequisite steps, mutex-timeout behavior and
uncorrelated ACK/retry policy. Device limits must come from source-grounded capability responses;
desktop fallback watts are not measured hardware limits.

Motion still needs physical scale, axes, timing, bias and upload-state ownership evidence. Desktop
gyro mapping persists profiles, whereas raw upload is separate. RGB has a volatile RAM-preview path,
with ROM persistence as a separate operation; neither belongs in automatic discovery. The Intel
sensor-hub extension is another discovery lead, not proof that it owns the controller gyro. No broad
VID/PID family matching or firmware mode switch should be added to input acquisition.

When work resumes, follow the existing device SDK/Lab ownership and manual-first policy. Attended
hardware work and deployment need the maintainer's direction. Confirm exact identity/firmware,
collection shapes, pad/rumble/buttons, power/fan effects, stop/suspend restoration and OEM-software
coexistence before recording a hardware pass.
