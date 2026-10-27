# Acer Predator Atlas 8 handoff

Stopped at the maintainer's request on 2026-10-27. All investigation was offline. No vendor program
was executed, device opened, service installed/connected, hardware command sent or deployment run.

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
  acknowledgements, mode/limit values, getter decoding and recovery constraints.
- [Predator Sense findings](../external/libhandheld/src/LibHandheld/Families/Acer/research/predator-sense-findings.md)
  preserve binary identities, controller/motion/RGB/calibration commands, lighting and
  native-service leads.
- [Managed power/fan callers](../external/libhandheld/src/LibHandheld/Families/Acer/research/power-fan-managed.md)
  preserve desktop enums, watt units, validation, service messages and fan controls.
- [Native agent and fan getters](../external/libhandheld/src/LibHandheld/Families/Acer/research/power-fan-agent-native.md)
  preserve exact function addresses, WMI bit packing, result handling, registry observations and
  unresolved fan-count discovery. Its final follow-up sections refine earlier bounded-pass unknowns.

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

## Next work

Finish fan-count discovery (INI/configuration versus WMI), mixed-fan mode handling and restoration
sequencing. Preserve exact per-fan behavior/speed, the full manual power profile and original active
mode. Avoid the vendor's unsafe continuation after failed prerequisite steps, mutex-timeout behavior
and uncertain-write retries. Device limits must come from source-grounded capability responses;
desktop fallback watts are not measured hardware limits.

Motion still needs physical scale, axes, timing and bias evidence. RGB/controller profile commands
include persistent writes and must not be used as discovery probes. No broad VID/PID family matching
or firmware mode switch should be added to input acquisition.

When work resumes, follow the existing device SDK/Lab ownership and manual-first policy. Attended
hardware work and deployment need the maintainer's direction. Confirm exact identity/firmware,
collection shapes, pad/rumble/buttons, power/fan effects, stop/suspend restoration and OEM-software
coexistence before recording a hardware pass.
