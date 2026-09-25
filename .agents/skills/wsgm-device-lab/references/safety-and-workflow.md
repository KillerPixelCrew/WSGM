# Safety and workflow

## Running the tool

The executable is `wsgm-device` (`AssemblyName` in `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj`).
Nothing installs it globally. From the WSGM root:

```powershell
dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj --configuration Release
src\WSGM.DeviceLab\bin\Release\net10.0-windows\win-x64\wsgm-device.exe --help
```

`dotnet run --project src/WSGM.DeviceLab --configuration Release -- <command>` also works. No
arguments, or `gui`, opens the Avalonia GUI instead of printing usage. The GUI and CLI share
`Application/DeviceLabApplication.cs`. The GUI's attended action offers `capability`, `haptic` and
`controller` (not `haptic-sweep`) and asks for the same typed `RUN HARDWARE`.
`eng/publish-device-lab.ps1` writes the portable tree to `publish/DeviceLab`. Run it only when a
publish is requested.

## Command classes

Use the dedicated paths below. Some commands that do not mutate hardware still observe the live
machine or execute plugin code. "Read-only" does not mean it can run without the operator.

### Offline analysis and generation

```powershell
wsgm-device candidates --from <inventory.json> --device-id <id>
wsgm-device inspect <capture.wsgmcap>
wsgm-device compare <before.wsgmcap> <after.wsgmcap>
wsgm-device correlate <capture.wsgmcap> --action <id> --sources <id,id>
wsgm-device fixture extract --from <capture.wsgmcap> --id <id> --out-dir <new-dir>
wsgm-device scaffold --from <capture.wsgmcap> --out-dir <new-dir> [--usb-instance <exact-id>]
wsgm-device validate <plugin-dir>
wsgm-device test sample
wsgm-device glyph import <plugin-dir>
wsgm-device pack <plugin-dir> --out <new-file.wsgmpkg>
```

None of these authorize a hardware mutation. They can write the requested output, so give each one a
new directory or file. `scaffold` needs `--usb-instance` when the capture holds more than one exact
USB endpoint. `validate` is static and never loads plugin code.

### Live machine observation

```powershell
wsgm-device doctor --out-dir <dedicated-dir>
wsgm-device inventory --out-dir <dedicated-dir>
wsgm-device inventory --out-dir <other-dedicated-dir> --shareable
wsgm-device probe-read --from <inventory.json>
```

`doctor` and `inventory` observe the current machine and write reports. Both inventory forms write
`inventory.json` as a create-new file, and `--shareable` writes only the redacted document. Use two
different directories when you need both. `probe-read --from` lists matching compiled probes. An
actual run is a hardware read and needs operator approval:

```powershell
wsgm-device probe-read --from <inventory.json> --run <probe-id> --out-dir <dedicated-dir>
```

Close the running WSGM shell session before an actual probe or any `test hardware` workflow. The
shell holds `Global\WSGM.DeviceOwner` for its whole lifetime, even with Device Integration disabled,
so switching integration off is not enough.

Each probe compiles exact family, endpoint, getter, request, response shape, range, repetition,
rate, deadline and an independent cross-check. It runs in an authenticated one-use hidden worker and
never falls back from a getter to a setter. For new hardware, add a reviewed source profile rather
than making those fields user-configurable. Candidate matching (`Inventory/KnownMsiClaw.cs`) and the
compiled probes (`Probes/ReadProbeProfiles.cs`) cover only the MSI Claw today. On any other
handheld, including the Ally X, expect a mismatch and no runnable probes.

### Code-loading boundary

```powershell
wsgm-device test plugin <plugin-dir> --from <inventory.json>
```

This validates, loads and constructs the plugin, then calls `DetectAsync`. It does not intentionally
mutate hardware, but plugin code runs with Device Lab's authority. The worker and its job object
contain crashes and deadlines; they are not a sandbox. Review the constructor and `DetectAsync` for
side effects, and never run this on an untrusted package.

### Attended capture

```powershell
wsgm-device capture run --recipe <recipe.json> --out-dir <dedicated-dir>
```

Capture refuses redirected I/O, non-interactive sessions and CI. It then requires the exact word
`OBSERVE` and keeps the private capture separate. It shows a bounded projection of every sanitized
shareable lane and requires the exact word `EXPORT` before it writes `.wsgmcap`.

Current limitation: the live capture factory (`ClosedObserveOnlyCaptureSource` in
`ObserveOnlyCaptureWorkflow`) observes the inventory snapshot only. It emits every other recipe kind
as unavailable until a reviewed observer is compiled and registered. That covers operator markers,
PnP, HID input, Raw Input, hooks, WMI, controller APIs, sensors, serial, processes, plugin events
and telemetry. A recipe is closed metadata; it cannot grant arbitrary HID, WMI or script execution.

### Sole mutation door

```powershell
wsgm-device test hardware <plugin-dir> --from <inventory.json> --state-dir <new-dir> `
  --action capability --capability <id> --value <semantic-value> [--instance <id>]
```

The other actions are `haptic` (one 250 ms pulse with zero-output cleanup), `haptic-sweep` and
`controller` (one acquisition with verified topology release). All of them accept `--instance`, and
only `capability` takes `--capability` and `--value`. The command refuses redirected I/O, CI and
every form of `--yes`. It then asks for the exact phrase `RUN HARDWARE`. After that it recollects
live identity and runs the static and owner preflight. That preflight refuses nonmatching identity,
active or unknown production ownership, non-elevation and a reused state directory. It reserves
`Global\WSGM.DeviceOwner` before it loads the plugin. Each invocation performs one selected workflow
and must restore, zero or release on every path. The bounded `haptic-sweep` runs for up to five
minutes and is the one deliberate multi-write exception. Never automate it.

Exit codes are `0` success, `64` usage, and `70` operation failure. Result JSON goes to stdout and
diagnostics to stderr.

## Output firewall and privacy

`Preflight/OutputPathPolicy.cs` refuses these targets:

- drive roots;
- the profile, Desktop, Documents, Downloads and OneDrive folders themselves;
- the repository root itself;
- `%LOCALAPPDATA%\WSGM` and anything under it;
- existing reparse points and existing output files;
- a reused `--state-dir`.

Use a new bounded directory for each task. Do not weaken this policy to allow a more convenient
path.

A private inventory or capture keeps exact identifiers for local diagnosis. `--shareable` and
capture export produce separate redacted values; redaction does not toggle a file in place. Check
the preview and the hash and count inventory before you approve an export.

Treat imported `.wsgmcap` files as untrusted bounded ZIPs (`Capture/CaptureBundleReader.cs`).
Validate them before analysis: schema, paths, entry count, expanded size, hashes, redaction marker,
event sequences, source and recipe references, and payload disposition. Imported bytes never define
a hardware operation.

## Evidence quality

For every finding record:

- device definition, board/SKU, firmware and exact endpoint identity;
- tool/WSGM/plugin commits and whether the run was private or shareable;
- action performed, neutral/control trials, time window, sample rate and loss/discontinuities;
- raw observation or hash, decoded hypothesis, independent cross-check, and counterexample;
- whether the fact is observed, inferred, or still requires attended validation;
- cleanup/restoration result.

Timing correlation ranks hypotheses. It does not prove that a WMI event, HID bit, keyboard chord or
process change belongs to the action. Repeat isolated trials and include a negative control case.

Record device facts next to the device they describe:

- **Claw:** the dated measurements are in `_plan/claw-8-a2vm-plugin.md` and the provenance is in
  `src/WSGM.Device.Msi.Claw8A2Vm/PROVENANCE.md`.
- **Ally family:** the remote-tester results are in the ROG Ally X sections of
  `_plan/implementation-todo.md`, and the pinned HHD/HC facts and the list of what a lab report
  must confirm are in `src/WSGM.Device.Asus.RogAlly/PROVENANCE.md`.

## Source map

All paths are under `src/WSGM.DeviceLab/`.

| Concern                  | Path                                                                                                                           |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------ |
| CLI and exit behavior    | `Cli/DeviceLabCli.cs`, `Program.cs` (GUI/worker dispatch)                                                                      |
| Shared GUI/CLI facade    | `Application/DeviceLabApplication.cs`; GUI in `Gui/MainWindow.cs`, `Gui/DeviceLabGui.cs`                                       |
| Staging and artifacts    | `Application/DeviceLabPaths.cs`, `Application/DurableFile.cs`                                                                  |
| Self-worker protocol     | `Application/SelfWorkerProtocol.cs`, `SelfWorkerAuthorization.cs`, `WorkerJobObject.cs`                                        |
| Hardware arguments       | `Cli/HardwareTestCliArguments.cs`                                                                                              |
| Identity/inventory       | `Inventory/`, especially `KnownMsiClaw.cs`, `WindowsInventoryCollector.cs`, and `ExtendedWindowsInventoryCollector.cs`         |
| Read probes              | `Probes/ReadProbeProfiles.cs`, `ReadProbePolicy.cs`, worker and supervisor                                                     |
| Capture model/runtime    | `Capture/CaptureModels.cs`, `ObserveOnlyCaptureWorkflow.cs`, `PassiveCapture.cs`, `CaptureBundleReader.cs`                     |
| Redaction and preview    | `Capture/Redaction.cs`, `InventoryRedaction.cs`, `CapturePrivacyPreview.cs`                                                    |
| Correlation and fixtures | `Capture/PassiveCorrelation.cs`, `Fixtures/FixtureExtractionWorkflow.cs`                                                       |
| Output/owner safety      | `Preflight/OutputPathPolicy.cs`, `SafetyPreflight.cs`, `WindowsPreflightInspection.cs`                                         |
| Hardware door            | `Testing/PluginTestWorkflow.cs`, `PluginTestWorker.cs`, `AttendedPluginAction.cs`                                              |
| Scaffolding/package      | `Scaffolding/`, `Packaging/`, `Templates/MinimalPlugin/`; package source shared from `src/WSGM/Interop/NativePackageSource.cs` |

Offline suite, run after the maintainer's manual test under the root validation policy:

```powershell
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj --configuration Release
```

## Portable Ally X bring-up

`tools/AllyXLab` is a self-contained EXE for an attended remote Ally X tester, built at the
maintainer's request. It is a separate developer tool. It is not a Device Lab command and not the
production plugin, and its README is authoritative. Keep these points in mind:

- It admits RC72LA and RC73XA only; RC73YA is refused. Its family gate is not the production
  plugin's firmware allowlist.
- HHD is the primary reference and HC cross-checks the Windows transport. Use the pinned tables in
  `src/WSGM.Device.Asus.RogAlly/PROVENANCE.md`. `_ref` may be missing from a checkout; when it is
  present, search it with `rg --hidden --no-ignore`.
- The tracked download lives in `tools/AllyXLab/Downloads` (`AllyXLab.exe`, `SHA256.txt`,
  `BUILD.json`). `eng/allyxlab-download.ps1` checks that the three files agree and warns about
  source drift, and `eng/verify.ps1` runs that check. `-Build` rebuilds all three from committed
  source and adds about 55 MB to history, so rebuild only when a tester needs the new binary.
- Its guard tests are in `tests/WSGM.AllyXLab.Tests`, outside `WSGM.slnx`.
- A build or publish does not authorize running it on this machine, and it proves no hardware
  behavior. Do not add generic raw command entry, unattended writes, or controller remapping without
  original-state restoration.
