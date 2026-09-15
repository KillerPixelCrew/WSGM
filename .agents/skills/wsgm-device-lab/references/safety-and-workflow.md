# Safety and workflow

## Command classes

Use dedicated explicit paths. Some non-mutating commands still observe the live machine or execute
plugin code; “read-only” does not mean “safe to run without the operator.”

### Offline analysis and generation

```powershell
wsgm-device candidates --from <inventory.json> --device-id <id>
wsgm-device inspect <capture.wsgmcap>
wsgm-device compare <before.wsgmcap> <after.wsgmcap>
wsgm-device correlate <capture.wsgmcap> --action <id> --sources <id,id>
wsgm-device fixture extract --from <capture.wsgmcap> --id <id> --out-dir <new-dir>
wsgm-device scaffold --from <capture.wsgmcap> --out-dir <new-dir>
wsgm-device validate <plugin-dir>
wsgm-device test sample
wsgm-device glyph import <plugin-dir>
wsgm-device pack <plugin-dir> --out <new-file.wsgmpkg>
```

These do not authorize hardware mutation. They can write the requested output, so use a dedicated
new directory/file. `validate` is static and never loads plugin code.

### Live machine observation

```powershell
wsgm-device doctor --out-dir <dedicated-dir>
wsgm-device inventory --out-dir <dedicated-dir>
wsgm-device inventory --out-dir <dedicated-dir> --shareable
wsgm-device probe-read --from <inventory.json>
```

`doctor` and `inventory` observe the current machine and write reports. `probe-read --from` lists
matching compiled probes; an actual run is an operator-approved hardware read:

```powershell
wsgm-device probe-read --from <inventory.json> --run <probe-id> --out-dir <dedicated-dir>
```

Close the running WSGM shell/session before an actual probe or any `test hardware` workflow.
`Global\WSGM.DeviceOwner` remains reserved for the shell's lifetime even when Device Integration is
disabled, so toggling integration off is insufficient.

Each probe compiles exact family, endpoint, getter, request, response shape, range, repetition,
rate, deadline, and independent cross-check. It runs in a disposable hidden self-worker and never
falls back from a getter to a setter. Add a reviewed source profile for new hardware rather than
making those fields user-configurable. The current candidate matcher and compiled probes are limited
to the known MSI Claw fingerprint; an unknown handheld can legitimately yield only a mismatch and no
runnable probes.

### Code-loading boundary

```powershell
wsgm-device test plugin <plugin-dir> --from <inventory.json>
```

This validates, loads, constructs, and calls `DetectAsync`. It does not intentionally mutate
hardware, but arbitrary plugin code executes with Device Lab's authority. Use only trusted task code
after reviewing the constructor and `DetectAsync` for side effects; do not use it on an untrusted
package.

### Attended capture

```powershell
wsgm-device capture run --recipe <recipe.json> --out-dir <dedicated-dir>
```

Capture requires exact `OBSERVE`, keeps the private capture separate, shows a bounded projection of
every sanitized shareable lane, and requires exact `EXPORT` before writing `.wsgmcap`.

Current limitation: the live capture factory implements inventory observation. Every other declared
recipe kind—including operator markers, PnP, HID input, Raw Input, hooks, WMI, controller APIs,
sensors, serial, processes, plugin events, and telemetry—is emitted unavailable until a reviewed
observer is compiled and registered. A recipe is closed metadata and cannot grant arbitrary
HID/WMI/script execution.

### Sole mutation door

```powershell
wsgm-device test hardware <plugin-dir> --from <inventory.json> --state-dir <new-dir> `
  --action capability --capability <id> --value <semantic-value>
```

Other explicitly selected attended workflows are `haptic`, `haptic-sweep`, and `controller`. Inspect
current command help for their arguments. The command refuses redirected I/O, CI, `--yes`,
nonmatching identity, active production ownership, non-elevation, and a reused state directory. It
recollects live identity, reserves `Global\WSGM.DeviceOwner`, and asks for exact `RUN HARDWARE`
immediately before activation. Each invocation performs one selected workflow and must
restore/zero/release on every path. The bounded, up-to-five-minute `haptic-sweep` is the deliberate
multi-write calibration exception. Never automate it.

Exit codes are `0` success, `64` usage, and `70` operation failure.

## Output firewall and privacy

Device Lab refuses drive roots, broad user folders, a repository root, `%LOCALAPPDATA%\WSGM`,
existing reparse points, unsafe overlap, and reused hardware state. Use new bounded task
directories; do not weaken this policy to accommodate a convenient path.

Private inventory/capture keeps exact identifiers for local diagnosis. `--shareable` and capture
export create separate redacted values; redaction is not an in-place toggle. Inspect the preview and
hash/count inventory before approving export.

Imported `.wsgmcap` files are untrusted bounded ZIPs. Validate schema, paths, entry count, expanded
size, hashes, redaction marker, event sequences, source/recipe references, and payload disposition
before analysis. Imported bytes never define hardware operations.

## Evidence quality

For every finding record:

- device definition, board/SKU, firmware and exact endpoint identity;
- tool/WSGM/plugin commits and whether the run was private or shareable;
- action performed, neutral/control trials, time window, sample rate and loss/discontinuities;
- raw observation or hash, decoded hypothesis, independent cross-check, and counterexample;
- whether the fact is observed, inferred, or still requires attended validation;
- cleanup/restoration result.

Timing correlation ranks hypotheses. It does not prove that a WMI event, HID bit, keyboard chord, or
process change belongs to the action. Repeat isolated trials and find a negative/control case.

## Source map

| Concern               | Device Lab path                                                                                                        |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| CLI and exit behavior | `src/WSGM.DeviceLab/Cli/DeviceLabCli.cs`                                                                               |
| Shared GUI/CLI facade | `Application/DeviceLabApplication.cs`                                                                                  |
| Hardware arguments    | `Cli/HardwareTestCliArguments.cs`                                                                                      |
| Identity/inventory    | `Inventory/`, especially `KnownMsiClaw.cs`, `WindowsInventoryCollector.cs`, and `ExtendedWindowsInventoryCollector.cs` |
| Read probes           | `Probes/ReadProbeProfiles.cs`, `ReadProbePolicy.cs`, worker and supervisor                                             |
| Capture model/runtime | `Capture/CaptureModels.cs`, `ObserveOnlyCaptureWorkflow.cs`, `PassiveCapture.cs`                                       |
| Correlation           | `Capture/PassiveCorrelation.cs`                                                                                        |
| Output/owner safety   | `Preflight/OutputPathPolicy.cs`, `SafetyPreflight.cs`, `WindowsPreflightInspection.cs`                                 |
| Hardware door         | `Testing/PluginTestWorkflow.cs`, `PluginTestWorker.cs`, `AttendedPluginAction.cs`                                      |
| Scaffolding/package   | `Scaffolding/`, `Packaging/`, `Templates/MinimalPlugin/`                                                               |

Offline suite:

```powershell
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj --configuration Release
```

## Portable Ally X bring-up

The maintainer explicitly requested `tools/AllyXLab` as a self-contained Windows EXE for a remote
Ally X tester. Its README defines the closed, attended workflows and recovery behavior. This is a
separate developer tool, not a new Device Lab CLI mutation command or a production plugin. HHD is
primary for Ally X behavior; HC is a Windows transport cross-reference. Use the pinned source
comparison in `src/WSGM.Device.Asus.RogAllyX/REFERENCE.md`.

Build/publish does not authorize running it on the current machine. The single Start flow guides
each action inline. The input section has no confirmation prompts: it listens on every input channel
at once (Raw Input for all devices and vendor pages, low-level hooks with injected flags, XInput with
the guide button, Windows.Gaming.Input, WMI firmware events, shell app commands, power settings) and
the press itself advances the step. Rumble probes each motor route with one short pulse and
calibrates on the route the tester confirmed feeling, instead of assuming one reference's path. The
session opens with a device-access check: conflicting managers are listed with their effect on the
evidence and may be asked to close through their window, never killed and never stopped as services,
and HidHide's allowed-application list gains this tool's entry only with the tester's agreement and
is written back unchanged at the end. Rumble is one bounded interactive six-phase
worker: Ready starts a phase and explicit felt/not-felt answers advance it. Missing readback or
unknown cleanup must remain explicit. Do not add generic raw command entry, unattended writes or
controller remapping without original-state restoration. The maintainer requested the compiled EXE
be tracked under `tools/AllyXLab/Downloads`; update its SHA-256 alongside the binary after
publishing reviewed source. Automated suites still wait for the maintainer's manual-testing report
unless explicitly requested sooner.
