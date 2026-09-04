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
| CLI and exit behavior | `external/WSGM.DeviceLab/src/WSGM.DeviceLab/Cli/DeviceLabCli.cs`                                                       |
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
dotnet test external/WSGM.DeviceLab/WSGM.DeviceLab.slnx --configuration Release
```
