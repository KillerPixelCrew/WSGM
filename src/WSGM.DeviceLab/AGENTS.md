# WSGM Device Lab contributor instructions

## Scope and sources of truth

These instructions apply to `src/WSGM.DeviceLab/**`.

Device Lab is a separate MIT-licensed Windows authoring and diagnostics application, not part of the WSGM runtime. The
Avalonia GUI and `wsgm-device` CLI must use the same application services and produce the same results. Before changing
behavior, read this project's `README.md`, the relevant tests, and the implementation being changed; keep all three
aligned.

## Build and dependency workflow

From the repository root:

```powershell
dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj --configuration Release
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj --configuration Release
```

The target is .NET 10 on Windows; release publishing is self-contained `win-x64`. Run
`./eng/publish-device-lab.ps1` only when a publish artifact is requested. The project version in
`WSGM.DeviceLab.csproj` is authoritative. Do not tag, release, or publish unless explicitly asked.

`src/WSGM.Device.Sdk` is the shared contract in this WSGM repository. Update the SDK and its consumers in the same pull
request. Scaffolding references the checked-out SDK project in a source checkout, or the exact SDK assembly beside the
running tool otherwise.

## Command and application contract

- No arguments and `wizard` start the tester wizard, which relaunches itself elevated once
  (`--elevated-relaunch` prevents a loop) and opens without elevation if the prompt is declined.
  `gui` starts the developer tabs as-invoker. Normal CLI commands are `doctor`, `inventory`, `candidates`,
  `probe-read`, `capture`, `inspect`, `compare`, `correlate`, `fixture`, `scaffold`, `glyph`,
  `validate`, `test`, `pack`, `report`, `review` and `promote` (the last three read a returned `.wsgmlab`; `scaffold --from` also takes one).
- `__read-probe` and `__plugin-test` are authenticated internal worker modes, not public commands.
- Use stdout for result JSON and stderr for diagnostics. Preserve exit codes: `0` success, `64`
  usage error, `70` operational failure.
- Keep long work cancellable and off the UI thread. Reject duplicate GUI operations, and do not erase the last
  successful result when a later operation fails or is cancelled.
- Add command behavior through `Application/` and the shared CLI/GUI services rather than parallel implementations.

## Safety boundary

Device Lab's built-in workflows are read-only by default. Imported inventory, recipes, fixtures, and packages are
untrusted evidence; use static validation until plugin code has been deliberately trusted.

- Compiled read probes require an exact live device and endpoint match, typed expectations and cross-checks, strict
  time/read limits, an authenticated one-use worker, and process-tree termination at the deadline. They must not write
  hardware or durable state.
- `test plugin` loads, constructs, and calls arbitrary plugin code with the user's authority. Its authenticated worker
  and job object contain crashes and deadlines; they are not a security sandbox or hardware-access boundary.
- `test hardware` is the only built-in workflow that intentionally requests a plugin mutation. It must be a local
  attended session, reject CI and non-interactive use, reject every form of `--yes`, require immediate confirmation of
  one explicit semantic action, recollect live identity, and use a new explicit state directory. Preserve the static
  refusal path before plugin code is loaded, while remembering that a malicious plugin can ignore the SDK contract once
  executed.
- Reserve the unowned `Global\WSGM.DeviceOwner` mutex before loading a hardware plugin. Hold it through cleanup and
  disposal; never wait on or release it across `await`. If construction, package identity, stop, or disposal is
  unverified, retain ownership for the process lifetime.
- A hardware action must capture original state, apply one action, verify readback, and restore/zero output/release
  before success. An unverified cleanup is a failure.
- Observe-only capture requires hash-bound approval of the local interactive observation scope, then a separate approval
  of the sanitized export preview before publication. Do not merge or bypass those approvals.

## Tester wizard

The wizard (`Wizard/`, `Gui/WizardWindow.cs`) is the one workflow that changes machine state
without a plugin; see the 2026-09-24 entry in `docs/decisions.md`.

- Record every machine change in `LabMachineState` before making it, and clear the record only
  after a readback shows the change undone. The next wizard start undoes whatever a killed session
  left. HidHide changes add or remove only the lab's own exact entry; never flip the hiding switch
  or edit an inverse-mode list.
- Other managers get a window close request, never a kill; services are never stopped.
- PawnIO is installed only from the embedded installer, extracted into a new administrators-only
  folder under the Windows temp directory and held open without write or delete sharing while its
  SHA-256 and Authenticode signer are checked against the embedded `external/pawnio/pawnio.lock.json`
  and while it runs. The uninstaller gets the same signer check. Never pass `-unrestricted`.
- Replace an older PawnIO only on the tester's explicit choice. `LabMachineState` is the one record
  of what the lab did: removal is offered only for a fresh install the lab made (never for a
  replacement), and the record is reconciled against what is installed when the wizard starts.
- Preflight reserves `Global\WSGM.DeviceOwner` and holds it until the window closes. Hardware stages
  refuse to start without that reservation.
- A project is a folder of attempts. Never overwrite evidence; a redo creates a new attempt, and the
  manifest is the only file rewritten (atomically).
- Input capture records every input from every device and attributes it afterwards. Never filter by
  device, VID/PID or usage page at capture time; the knowledge base may rank sources, not narrow them.
  `Capture/Live/LabInputCapture` is the one capture: Raw Input on every usage page present, low-level hooks,
  XInput, Windows.Gaming.Input, WMI events, power and device changes. While a button step runs it
  swallows Windows-key and Alt+Tab shortcuts after recording them, so a firmware chord cannot
  minimize the wizard; it suppresses nothing else. Presses are counted by key-up.
  Storage per step is bounded; baseline-noise reports are sampled and everything else that did not
  fit is counted, never silently lost. Hiding pointer and touch input is for display only.
- A Curated record's controller init (`LabControllerInit`) runs before the buttons stage. A
  reversible one (the Claw mode switch) is recorded before it is sent and switched back at the end
  and on the next start; an irreversible one (the Ally button tables) is sent only on the tester's
  explicit choice and is never described as undone.
- Without a curated record, `Wizard/LabModeCommands` offers HC's own mode and init commands (Legion
  Go mode, touchpad and gyro, Steam Deck and Steam Controller lizard mode and gyro, GameSir test
  mode, OXP remap pages, ZOTAC M1/M2) as a compiled, reviewed table with HC/HHD file:line
  provenance, never from inventory. Each is sent only on the tester's "Try it", only to the one
  collection that matches exactly, and never retried; a reversible one is recorded before it is
  sent and undone at the stage end and on the next start. Never add an EC write, a factory reset
  or a command whose meaning is unknown; list it as withheld instead.
- `Transports/LabLhmSensors` reads fan RPM and temperatures through LibreHardwareMonitor for every
  device. It never sets a control, and its controller and PSU groups stay off because their
  discovery writes to FTDI serial ports and vendor USB HID devices.
- The sleep stage waits for the tester's power button; the wizard never requests sleep itself.
- Hardware stages (buttons, motion, rumble, power, sleep) start through `RunHardware`, which refuses
  without the preflight owner reservation. Output writes (rumble, TDP, fans, charge limit, lighting)
  use only generic Windows APIs or a Curated record's typed mechanism whose endpoint is present;
  never guess reports, registers or WMI methods, and never write the EC. Every write is bounded,
  read back where the transport can, restored on every exit path, and recorded in `LabMachineState`
  before it is made when a crash could leave it applied. An uncertain write is not retried; the
  tester gets an explicit button instead.
- Wizard hardware access runs in the elevated `Worker/LabWorkerHost` through a service interface
  registered in `LabWorkerServices` (the power transports live in `Transports/`); only interface
  methods are callable. A `[LabWorkerWrite]` method is refused until a checkpoint is acknowledged:
  the worker captures the original with the service's `[LabWorkerSnapshot]` method, the wizard
  records it (`LabPowerRecovery.Record`) and acknowledges within five seconds, and releases the
  checkpoint only after a verified restore. Start-up recovery runs through the worker too. A missed
  deadline (`LabWorkerLostException`) is uncertain: never retry it.
- The shared report is built in memory, previewed, and written once. Every JSON string passes through
  one `CaptureRedactor`; only JSON and raw ACPI tables leave the machine.
- The wizard keeps blocking work (files, registry, drivers) off the UI thread and runs one operation
  at a time; a failure is shown on the page, never swallowed. Selecting a stage only shows it; a new
  attempt starts only from Start or Run again. Closing waits for the running operation, then undoes
  the session's HidHide entry and releases the owner reservation.

## Filesystem and artifact rules

- User-created workflow artifacts use new, non-reparse, owned output targets. The marked publish tree managed by
  `eng/publish-device-lab.ps1` is the explicit exception and may be atomically replaced after ownership checks. Reject
  drive and filesystem roots, the broad home directories themselves, the repository root itself, and the live
  `%LOCALAPPDATA%\WSGM` tree, including its descendants.
- Use staging plus atomic publication and create-new semantics. Never overwrite an unrelated target or follow a reparse
  point.
- Keep private captures separate from shareable redacted output. Preserve deterministic hashing, archive ordering,
  retained-input evidence, and preview/count/hash consistency.
- Treat correlation as bounded candidate evidence, never proof of causation.

## Knowledge records

- `Knowledge/Devices/hc.*.json` is generated output of `eng/extract-hc-devices.ps1` from
  `_ref/HandheldCompanion`. Never edit those files by hand; change `tools/HcDeviceExtract` and
  regenerate. The extractor reads declarations only and records HC's mistakes as hazards.
- `Knowledge/Devices/wsgm.*.json` is curated. Every fact carries provenance naming the plugin file,
  lab run or HC line it came from. Keep a curated record in step with the plugin it cites.
- A record is evidence, not a driver. Only curated records may carry wizard button mappings,
  readback claims or supersede an extracted record; the loader and tests enforce that.
- Compiled read probes are gated by a curated record (`DeviceKnowledgeAssessor`): its exact
  identity rule, controller endpoints and WMI provider. Keep a probe family's
  `KnowledgeRecordId` pointing at a curated record; there is no separate device fingerprint.
- The parser rejects unknown members. Extend `DeviceKnowledge.cs` and bump the schema version rather
  than loosening it.

## Package and scaffold rules

- Package validation is static and must never load plugin code. Keep manifest/layout, managed-x64 PE, entry-count,
  file-size, aggregate-size, and prohibited-file checks bounded and fail closed.
- Retain opened input handles through validation and packing so validated bytes are the bytes published. Keep packages
  deterministic.
- Generated projects, manifests, tests, glyph profiles, and documentation must agree on package ID, API version, target
  framework, and SDK reference.

## Change discipline

Prefer semantic records and deterministic services over device-specific special cases. Keep hardware policy out of UI
code. Update focused unit tests for every changed invariant, especially refusal paths, worker authentication/deadlines,
mutex lifetime, path ownership, package limits, deterministic output, and cancellation. Preserve the repository
`.editorconfig` conventions.
