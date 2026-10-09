# Safety and workflow

## Running the tool

The executable is `wsgm-device` (`AssemblyName` in `src/WSGM.DeviceLab/WSGM.DeviceLab.csproj`).
Nothing installs it globally. From the WSGM root:

```powershell
dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj --configuration Release
src\WSGM.DeviceLab\bin\Release\net10.0-windows10.0.19041.0\win-x64\wsgm-device.exe --help
```

`dotnet run --project src/WSGM.DeviceLab --configuration Release -- <command>` also works. No
arguments, or `wizard`, starts the tester wizard and requests elevation once; `gui` opens the
as-invoker developer tabs. The GUI and CLI share `Application/DeviceLabApplication.cs`.
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
```

None of these authorize a hardware mutation. They can write the requested output, so give each one a
new directory or file. `scaffold` needs `--usb-instance` when the capture holds more than one exact
USB endpoint. Scaffolding emits reviewed library source, not an executable package.

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

Close the running WSGM shell session before an actual probe or an attended wizard hardware session.
The shell holds `Global\WSGM.DeviceOwner` for its whole lifetime, even with Device Integration
disabled, so switching integration off is not enough.

Each probe compiles exact family, endpoint, getter, request, response shape, range, repetition,
rate, deadline and an independent cross-check. It runs in an authenticated one-use hidden worker and
never falls back from a getter to a setter. For new hardware, add a reviewed source profile rather
than making those fields user-configurable. Candidate matching uses the curated/extracted records
under `Knowledge/Devices` and covers many handhelds. Compiled getter profiles in
`Probes/ReadProbeProfiles.cs` currently provide only the MSI Claw family, gated by
`DeviceKnowledgeAssessor` and its curated record. A matching Ally candidate does not create a
runnable compiled probe.

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

### Attended wizard mutation

Hardware writes occur only in the local attended wizard and its authenticated, checkpointed worker.
Each named operation checks exact identity, captures available originals, records pending changes
before dispatch and restores or zeroes output on every exit. Keep failed restoration evidence. Do
not automate confirmation or turn an imported recipe into a hardware command.

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
  `external/libhandheld/src/LibHandheld/Families/MsiClaw/PROVENANCE.md`.
- **Ally family:** the remote-tester results are in the ROG Ally X sections of
  `_plan/implementation-todo.md`, and the pinned HHD/HC facts and the list of what a lab report must
  confirm are in `external/libhandheld/src/LibHandheld/Families/RogAlly/PROVENANCE.md`.

## Source map

All paths are under `src/WSGM.DeviceLab/`.

| Concern                  | Path                                                                                                                           |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------------ |
| CLI and exit behavior    | `Cli/DeviceLabCli.cs`, `Program.cs` (GUI/worker dispatch)                                                                      |
| Shared GUI/CLI facade    | `Application/DeviceLabApplication.cs`; GUI in `Gui/MainWindow.cs`, `Gui/DeviceLabGui.cs`                                       |
| Staging and artifacts    | `Application/DeviceLabPaths.cs`, `Application/DurableFile.cs`                                                                  |
| Self-worker protocol     | `Application/SelfWorkerProtocol.cs`, `SelfWorkerAuthorization.cs`, `WorkerJobObject.cs`                                        |
| Hardware arguments       | `Cli/HardwareTestCliArguments.cs`                                                                                              |
| Identity/inventory       | `Inventory/`, `WindowsInventoryCollector.cs`, `ExtendedWindowsInventoryCollector.cs`; identity rules in `Knowledge/`           |
| Read probes              | `Probes/ReadProbeProfiles.cs`, `ReadProbePolicy.cs`, worker and supervisor                                                     |
| Capture model/runtime    | `Capture/CaptureModels.cs`, `ObserveOnlyCaptureWorkflow.cs`, `PassiveCapture.cs`, `CaptureBundleReader.cs`                     |
| Redaction and preview    | `Capture/Redaction.cs`, `InventoryRedaction.cs`, `CapturePrivacyPreview.cs`                                                    |
| Correlation and fixtures | `Capture/PassiveCorrelation.cs`, `Fixtures/FixtureExtractionWorkflow.cs`                                                       |
| Output/owner safety      | `Preflight/OutputPathPolicy.cs`, `SafetyPreflight.cs`, `WindowsPreflightInspection.cs`                                         |
| Scaffolding/package      | `Scaffolding/`, `Packaging/` (also `NativePackageSource.cs` and `NativePathIdentity.cs`, Lab-only), `Templates/MinimalPlugin/` |

Offline suite, run after the maintainer's manual test under the root validation policy:

```powershell
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj --configuration Release
```

## Remote testers and the wizard

A remote tester runs the portable `wsgm-device.exe` (`eng/publish-device-lab.ps1 -Portable`) and
lands in the wizard, which elevates itself once. It replaced the Ally-only AllyXLab tool. Keep these
points in mind:

- The wizard is attended. Its hardware stages change machine state (HidHide, PawnIO, controller
  mode, rumble, power limits, fans, charge limit, lighting) under the rules in
  `src/WSGM.DeviceLab/AGENTS.md`. A build or publish does not authorize running it on this machine,
  and a returned report proves only what its evidence shows.
- Read a returned report with `wsgm-device report`, `review` and `promote`; `scaffold --from` takes
  it too. Promote only after reviewing the disagreements.
- HC is the primary Windows-native Ally reference, including buttons; HHD cross-checks behavior HC
  does not cover. Use the pinned tables in
  `external/libhandheld/src/LibHandheld/Families/RogAlly/PROVENANCE.md`. `_ref` may be missing from
  a checkout; when it is present, search it with `rg --hidden --no-ignore`.

## Wizard worker and evidence ownership

The tester wizard is separate from the CLI observe-only recipe workflow. Its current live input
capture is `Capture/Live/LabInputCapture*`: Raw Input, keyboard/mouse hooks, XInput,
Windows.Gaming.Input, DirectInput and WMI observations are collected broadly and attributed later. A
knowledge record can rank likely endpoints but must not filter the capture to one device.
`Capture/Live/LabMotionRecorder*` and `LabRumble*` provide the dedicated motion and rumble stages.

`Wizard/LabMachineState.cs` records machine changes before mutation and retains incomplete cleanup
for next-start recovery. `Wizard/LabProject.cs` stores separate attempts rather than overwriting an
earlier capture. `Reports/LabReport.cs` builds the returned evidence. A requested report review is
offline work; it does not authorize a fresh wizard run or probe on the current machine.

Writes with readable originals run through `Worker/LabWorkerHost.cs` and the interfaces registered
by `Worker/LabWorkerServices.cs`. The worker captures a snapshot, the wizard durably records it
through `Wizard/LabPowerRecovery.cs`, and acknowledgment opens the write checkpoint. A missed worker
deadline is uncertain; do not retry. Cancellation ends waiting, not an already-dispatched hardware
operation. Rumble streams keep their watchdog zero armed until a zero write succeeds.

Keep the wizard's stricter restore/readback accounting separate from normal plugin command
semantics: a production `AppliedUnverified` result can be successful while the lab cannot claim
verified hardware acceptance. Never fabricate a verified pass to make the test harness green.
