# Device Lab attended wizard (AllyXLab consolidation)

Status (2026-09-25): delivered. AllyXLab was retired in `829c5a5c`, and the work landed in one pull
request rather than the seven listed under "Delivery"; `_plan/implementation-todo.md` tracks what is
left. The text below is the plan as written, so its present tense about AllyXLab is historical.

## Context

Device Lab (`src/WSGM.DeviceLab`, Avalonia + CLI) is a plugin authoring and diagnostics tool, and
its live capture is mostly a stub. AllyXLab (`tools/AllyXLab`, WinForms single-file) is a working
attended tester for a single device family. We want one tool the maintainer can send to people with
any handheld:

- It walks them through an attended setup.
- It exports a project that can be reopened to redo single segments.
- It carries a knowledge base of known devices, extracted from Handheld Companion 1.3.1.6
  (decompiled into `_ref/HandheldCompanion`).
- Once a device's data is confirmed, Device Lab's scaffold turns it into a plugin project.

AllyXLab is retired once the wizard covers everything it does.

## What HC taught us (drives the design)

The full notes from the three surveys (motion, buttons, fan/RGB/TDP) are written to
`_ref/HandheldCompanion/FINDINGS.md` as the first implementation step. Summary:

- **HC's device JSON is IMU-only.** `Resources/Devices/*.json` hold only `GyroMatrix`,
  `AcceleroMatrix` and the legacy sensor field maps. The following are all hard-coded in the C#
  device classes (`HandheldCompanion.Devices.*`):
  - capabilities (the `DeviceCapabilities` flags)
  - EC registers (`ECDetails`)
  - WMI/ATKACPI IDs
  - HID layouts
  - button sources and init commands

  So the knowledge base is extracted from the classes, not imported from JSON.

- **HC's data has bugs, so the wizard measures and never trusts:**
  - Hyphenated GPD JSONs are never loaded.
  - LegionGo and LegionGoSZ2 get identity axes.
  - OneXPlayerApex has an invalid axis swap.
  - LegionGoTablet clips gyro at 124 °/s.
  - HC never reads fans or TDP back.

  Knowledge-base values are `hc-derived` until the wizard confirms them, and the report lists every
  disagreement.

- **Motion arrives through four paths we care about:**
  1. WinRT `Gyrometer` / `Accelerometer`
  2. Legacy COM Sensor API, including the Claw's `SENSOR_TYPE_CUSTOM` "Physical Gyrometer" fields
     (FormatId `b14c764f-…`, PIDs 7/8/9)
  3. A CH340 serial IMU (0x1A86:0x7523, 115200 baud, 23-byte `A4 03 08 12` frames)
  4. Controller HID reports (Legion Go/Go S, Steam Deck Neptune, Steam Controller, GameSir)

  HC's fifth path, SDL, only serves external pads (DualSense, DS4, Switch Pro). Those are not the
  device under test, so the wizard does not ship SDL.

- **OEM buttons arrive through five paths:**
  1. Firmware keyboard chords, caught by a global LL keyboard hook. This is how most AYANEO, GPD,
     OXP, Loki and Zotac buttons arrive, plus the Ally's M1/M2 after they are remapped to F18/F17.
  2. Vendor HID input reports read by the device class: Ally report `0x5A`, OXP command `0xB2`, and
     GPD Win 5 on firmware ≥ 1.17.
  3. Controller HID parsing: Legion, Deck, Zotac dials, and Claw DInput M1/M2.
  4. The WMI `MSI_Event` (Claw).
  5. The XInput hidden Guide (ordinal #100).

  Volume and power buttons have no path in HC. Many buttons only report after an **init command**:
  - Ally: gamepad mode plus button tables
  - Claw: mode switch, plus the `MSI_Event` MOF fix
  - Legion: mode and passthrough
  - Deck: lizard mode off, re-sent every second
  - OXP: remap pages plus EC turbo takeover
  - Zotac: M1/M2 remap

- **Evidence from the AllyXLab 0.2.1 run of 2026-09-15** (Xbox Ally X RC73XA, BIOS 317, EC 3.14,
  folder `Downloads\AllyXLab-20260915-171651-7db7c7`):
  - **OEM buttons:** Command Center and Armoury Crate arrived as F21/F22 and M1/M2 as F18/F17, all
    on the ASUS 0B05:1B4C keyboard collection through Raw Input. The 0x5A vendor report path was
    never read.
  - **Volume keys** were seen only by the LL hook. Raw Input was filtered to the ASUS device, so
    their source device is unknown. **Power** produced no input; it needs `WM_POWERBROADCAST` /
    console display state.
  - **Gamepad steps recorded nothing** because the device exposed no HID gamepad collection and no
    XInput slot, and 0.2.1 listened only on ASUS HID plus XInput. AllyXLab 0.3.0 already rebuilt
    capture to listen on every channel (Raw Input for all devices, hooks, XInput with guide, WGI,
    WMI events, power notifications) and stopped discarding poses with few samples; 0.3.1 added the
    conflict check and 0.3.2 the HidHide handling. The wizard ports 0.3.2, not 0.2.1.
  - **Still open after 0.3.2:** key auto-repeat inflates counts (count by key-up), the legacy
    accelerometer produced one sample in 15 s (poll at the sensor's minimum report interval or
    subscribe), and the 0x5A / FF31:0080 paths are unverified on the Xbox Ally X.
  - **Consequence:** before each step the wizard reports which sources are alive (XInput slot, WGI
    gamepad, vendor collections, sensor sample rate) and records that list. A missing XInput slot is
    not an error on a device that has none; a step that ends with no source seeing anything is
    flagged, never silently stored as empty.
  - **Xbox Ally X is its own record.** HC's `XboxROGAllyX` inherits `ROGAlly`, but the 0.2.1
    inventory showed no gamepad collection, an FF31:0080 collection without an output report and a
    Dynamic Lighting collection. Its rumble and RGB paths differ from the RC72LA.
- **Fan, RGB, TDP and charge-limit transports:**
  - ATKACPI IOCTL (ASUS)
  - WMI methods: `MSI_ACPI`, `LENOVO_*`, OXP `SuRwECRegInterface`
  - ACPI EC at 0x62/0x66
  - Indirect EC RAM through SuperIO at 0x4E/0x4F or 0x2E/0x2F
  - HID output and feature reports
  - CH340 serial (OXP X1 LEDs)
  - Physical memory (Steam Deck)
  - PawnIO modules (RyzenSMU, LedsValve)
  - KX.exe MCHBAR/MSR access (Intel)
- **AMD SMU.** Message IDs per `CpuCodeName` come from `Processors/AMDProcessor.cs`: STAPM, fast and
  slow are 0x14/0x15/0x16 on Renoir through Strix, and 0x1A/0x1B/0x1C on Picasso and Raven. Mailbox
  addresses also vary by codename. EC and LPC access in HC goes through LibreHardwareMonitorLib
  (`LpcIo`, `WindowsEmbeddedControllerIO`), which is backed by PawnIO.
- **HC hazards we must not copy:**
  - `AMDProcessor` applies Curve Optimizer −1 as a "probe".
  - `TryGet*Limit` sends the _set_ command with 0.
  - Claw `Open()` writes PL limits.
  - The Deck OEM TDP mixes W and mW.
  - Some `ReadFanDuty` implementations read the wrong I/O ports.

  Our readbacks come from real sources: SMU PM table / MCHBAR / MSR 0x610 reads, and LHM or vendor
  fan RPM.

## Finished product

- **Who uses it.**
  - A tester runs the portable `wsgm-device.exe` (single file) and lands in the wizard. The wizard
    relaunches itself elevated on entry when it is not; the CLI (scaffold, validate, pack) stays
    as-invoker and needs no manifest change.
  - Developer mode keeps the existing tabs and the CLI, for reviewing projects and scaffolding
    plugins.
- **Project file changes.** Target framework moves to `net10.0-windows10.0.19041.0` for
  `Windows.Gaming.Input` and `Windows.Devices.Sensors` (AllyXLab already uses it; the SDK stays
  `net10.0-windows`). The portable publish uses `PublishSingleFile` with
  `IncludeNativeLibrariesForSelfExtract` for Avalonia's native libraries; the installer component
  keeps the folder publish.
- **Decision record.** DeviceLab's AGENTS.md says the tool is read-only apart from `test hardware`
  through a plugin, and the old `_plan/2.0-decisions.md` D08 says plugins own hardware.
  `_plan/2.0-decisions.md` is outdated (last touched 2026-09-08); `docs/decisions.md` is the live
  record. PR 2 adds an entry there: Device Lab may drive hardware for attended evidence collection
  on the tester's machine, under the same snapshot, readback, restore and no-retry rules a plugin
  must follow, and the knowledge base is evidence, not a plugin. It also rewrites the safety
  boundary in `src/WSGM.DeviceLab/AGENTS.md`. Every later PR updates that file and the README for
  what it adds, per the repository rule. Nothing in this work cites `_plan/2.0-decisions.md` as
  authority.
- **Project.**
  - A lab project is a folder, exported as a `.wsgmlab` ZIP. It holds a manifest, one directory per
    segment (status, answers, raw evidence, analysis) and a sanitized report.
  - Reopening a project shows every segment's status. Any segment can be redone, down to a single
    button, without touching the others. Redoing a sub-segment re-runs its stage's prerequisites
    (the button init sequence, the source liveness check). Reversible init is restored afterwards;
    unreadable Ally button tables require an explicit choice and are recorded as persistent.
  - Export always shows a privacy preview first. This reuses `Capture/Redaction.cs`,
    `InventoryRedaction.cs` and `CapturePrivacyPreview.cs`.
- **Knowledge base.** Embedded JSON records, one per HC device class (split where the hardware
  differs, as with Xbox Ally X) plus our seeds. The extractor fills every record automatically with
  what it can parse. Hand-curated init sequences, HID layouts and write ops exist only for
  tester-backed devices: Claw 8 A2VM, ROG Ally X and Xbox Ally X. All other records ship marked
  `incomplete` and get read-only stages only. Each record has:
  - Identity match rules, taken from HC's detection (manufacturer/product strings in
    `IDevice.GetCurrent`, board, SKU, USB VID/PID).
  - Motion sources with their axis maps and legacy sensor field maps.
  - Buttons: for each button, its source kind (chord keys, HID report ID/byte/mask, WMI event code,
    controller parser), plus the init and restore command sequences.
  - Fan, RGB, TDP and charge-limit mechanisms, written as data ops against generic transports. For
    example: `{transport: "superio-ec", index: 0x4E, data: 0x4F, reg: 0x44B, min: 0, max: 184}`.
  - The HC hazards that apply to the device, as notes.
  - Per-field provenance (`hc-derived` with class and file, `wsgm-plugin`, or `lab-confirmed`) and a
    confirmation state.
- **Wizard stages.** Each stage is a resumable segment.
  1. **Preflight.**
     - Elevation.
     - Interfering programs: port `Conflicts.cs`, which only closes windows and never kills a
       process.
     - HidHide self-allow with crash-safe undo: port `HidHideAccess.cs` and its pending-entry
       recovery.
     - Hold `Global\WSGM.DeviceOwner`.
     - Restore any outstanding write or init checkpoint.
     - Ensure PawnIO is installed (see "PawnIO").
  2. **Identity.**
     - Collect anonymized board, BIOS, EC, controller firmware and CPU codename.
     - Match against the knowledge base. On a match the tester confirms it. On no match the tester
       types the product name and exact model.
     - Never collect serials, UUIDs or MACs.
  3. **System dump.** Everything here is read-only.
     - ACPI tables from `HKLM\HARDWARE\ACPI` (which holds every SSDT keyed by OEM, table ID and
       revision; `GetSystemFirmwareTable` returns only the first table per signature) plus
       `GetSystemFirmwareTable` for anything the registry lacks. `MSDM` and `SLIC` are excluded.
       SMBIOS with serial, asset-tag, UUID and part-number strings stripped from types 1, 2, 3, 4,
       17 and 22.
     - The PnP device tree, all HID devices (descriptors and caps), serial ports, and WMI classes
       and methods (including `MSI_ACPI`, `LENOVO_*` and `SuRwECRegInterface` presence).
     - Sensors through WinRT and the legacy COM API, with every supported field of every sensor
       listed, which is how the Claw's custom fields were found.
     - A read-only dump of the 256 ACPI EC registers, to correlate with the DSDT's EC
       `OperationRegion`.
     - Display (native orientation, modes, VRR, brightness) and battery (capacities, charge-limit
       support).
     - AMD SMU codename and version, or the Intel MCHBAR base and MSR 0x610.
     - Reuses `WindowsInventoryCollector` / `ExtendedWindowsInventoryCollector`.
  4. **Buttons.**
     - **Known device:** snapshot and restore a reversible init at the end of the stage and on
       recovery. If the original button table cannot be read, explain that it cannot be restored and
       send the curated init only after the tester explicitly chooses it.
     - Walk the Xbox 360 layout including Guide. Then OEM left/right, back L1/L2/R1/R2, touchpad
       1/2, stick touch, volume up/down and power, each with Skip.
     - **Capture is never filtered by device.** Every step records every input that arrives: Raw
       Input from all keyboards, mice and every HID usage page (RIDEV_INPUTSINK plus RIDEV_PAGEONLY
       registrations), the LL keyboard and mouse hooks, all XInput slots, all WGI controllers and
       every WMI event. Attribution to a device happens afterwards in analysis; a filter at capture
       time is what lost the volume keys in the 0.2.1 run. The knowledge base only ranks sources, it
       never narrows them. This rule goes into DeviceLab's AGENTS.md with PR 4.
     - Every press listens on all paths at once, as AllyXLab 0.3.x does: the LL keyboard hook with
       VK, scancode and extended flag; HID report diffs on every vendor interface; Raw Input from
       all devices; XInput plus ordinal #100; WGI; `MSI_Event` and other WMI events; and
       power-button notifications. Presses are counted by key-up so auto-repeat does not inflate
       them.
     - Record press, release and hold. Firmware shortcuts (Win+D, Win+G, Ctrl+Alt+Tab and so on) are
       swallowed while a step is active.
     - Triggers get a full-travel sweep. Sticks get center, range and circularity. Touchpads also
       record their surface reports (Raw Input mouse or digitizer), not only the click.
     - For unknown devices, try the mode commands only when the tester opts in.
     - Port AllyXLab 0.3.2 `InputSources*.cs`, `InputCapture.cs`, `Hid.cs` and `InputSteps.cs`.
  5. **Motion.**
     - Run all four sources in parallel. Don't open a CH340 that the knowledge base assigns to
       device control, as on the OXP X1. Legacy COM sensors are polled at their minimum report
       interval, since a one-shot read gave a single sample in the 0.2.1 run.
     - Flat surface: a few seconds of bias and noise.
     - Six gravity poses plus yaw/pitch/roll. These derive the axis swap and signs per source, which
       are compared with the knowledge base. Port `Analysis.cs`.
  6. **Rumble.**
     - Route probe (HID, XInput or WGI; port `Motors.cs`), then confirm which motor is left and
       which is right.
     - Slider page: left and right, 0 to 100 %, rumbling live. The tester marks the lowest intensity
       they can feel. Live sliders need a streaming "set intensity" message to the worker, separate
       from the request/ACK protocol and its per-step deadlines; the worker zeroes the motors when
       the stream stops or the page is left.
     - Pulse page: per-side buttons at max and at the measured minimum, at several lengths.
     - Reuse `RumbleCalibration.cs` / `HapticSweep`.
  7. **Power and features.** Every write is bounded, snapshotted, checkpointed, read back from a
     real source, restored, confirmed by the tester, and never retried automatically. Tests run on
     AC and on battery.
     - **Known device:** knowledge-base-driven fan (RPM readback via LHM or vendor), RGB (the tester
       confirms the colour), TDP (OEM path or SMU/KX) and charge limit.
     - **Unknown AMD:** PawnIO RyzenSMU, using the codename table and mailbox from
       `AMDProcessor.cs`. Set STAPM, fast and slow to a conservative value, read back through the PM
       table, then restore.
     - **Unknown Intel:** KX.exe. Read the MCHBAR PL1/PL2 and MSR 0x610, do a bounded write, read
       back, then restore.
     - **Unknown fan and RGB:** no writes. The EC dump and ACPI tables are the evidence.
  8. **Sleep/resume.** The tester presses the power button; the wizard never calls
     `SetSuspendState`. After wake, confirm the controller, HID endpoints and sensors reappear, and
     whether init commands have to be re-sent.
  9. **Finish.** Privacy preview, the `.wsgmlab` ZIP on the Desktop, and the optional "Remove
     PawnIO".
- **Developer path.** Open a `.wsgmlab` file, review the disagreements with the knowledge base, and
  promote fields to `lab-confirmed`. Then scaffold from the project. This extends `scaffold` and
  `Templates/MinimalPlugin` so identity, button map, axis maps and capabilities are prefilled.

## PawnIO

- **Detection.** Read the uninstall key `HKLM\...\Uninstall\PawnIO` (`DisplayVersion`,
  `InstallLocation`) and open `\\.\PawnIO`.
- **Missing.**
  1. Tell the tester in one line.
  2. Extract the embedded pinned `PawnIO_setup.exe` 2.2.0 and verify its SHA-256 and namazso
     Authenticode signature.
  3. Run `-install -silent` and check the exit code.
  4. Re-detect.
  5. Record `pawnio-installed-by-lab`.

  Never pass `-unrestricted`.

- **Older version.** Ask the tester. On yes, run `uninstall.exe -uninstall -silent` from
  `InstallLocation`, then install.
- **Failure or driver blocked.** Log it. Mark the SMU, EC and SuperIO tests "unavailable: PawnIO";
  every other stage still runs.
- **Removal.** Leave PawnIO installed. The finish page offers removal only when the lab installed
  it.
- **Modules and pins.**
  - RyzenSMU.bin comes from namazso's signed PawnIO.Modules release (LedsValve is not needed).
  - EC and LPC access come from LibreHardwareMonitorLib 0.9.6 on NuGet (MPL-2.0, the version HC
    ships; its PawnIO backend is what HC's `OpenLibSys` uses). It also provides temperatures and fan
    RPM for readback.
  - `PawnIO_setup.exe` 2.2.0 is taken from the upstream release; its hash must equal HC's bundled
    copy.
  - KX.exe is unsigned and has no version resource, so it is pinned by SHA-256 with its origin and
    licence status recorded.
  - `external/pawnio/` and `external/kx/` hold the binaries, hashes and notes; the pin check in
    `eng/verify.ps1` covers them, and `THIRD_PARTY_NOTICES.md` in DeviceLab lists LHM, PawnIO and
    KX.
  - The controller HID readers use the Windows HID API already in DeviceLab and AllyXLab, not
    hidapi.

## Components and owners

- `src/WSGM.DeviceLab/Knowledge/`: schema, embedded records and matcher. Replaces `KnownMsiClaw` /
  `KnownDeviceMatcher`.
- `tools/HcDeviceExtract/` plus `eng/extract-hc-devices.ps1`: a development-time Roslyn walker over
  `_ref/HandheldCompanion/source/HandheldCompanion/HandheldCompanion.Devices.*`. It pulls
  `OEMChords`, `ECDetails`, capability flags, VID/PID, detection strings and constant tables, and
  merges the IMU JSON. It writes knowledge-base drafts with file and line provenance. Anything it
  cannot parse (HID layouts inside methods, init sequences) is hand-curated with provenance. Output
  is committed.
- `src/WSGM.DeviceLab/Transports/`: generic primitives the knowledge-base ops run on. HID (input,
  output, feature), WMI method and event, ATKACPI IOCTL, PawnIO (RyzenSMU), LHM EC and SuperIO,
  serial, KX.exe runner. Require snapshot and restore for reversible writes; an unreadable
  controller button table is an explicit opt-in persistent change.
- `src/WSGM.DeviceLab/Wizard/`:
  - the stage engine, project model and serializer, and Avalonia views
  - the elevated worker, which reuses `ReadProbeWorkerSupervisor` / `WorkerJobObject` with
    AllyXLab's checkpoint protocol
- `src/WSGM.DeviceLab/Capture/Live/`: ported AllyXLab 0.3.2 input, sensor and rumble capture, plus
  the new serial IMU and controller-HID readers.
- Licensing: Device Lab is MIT. AllyXLab and the WSGM HidHide code are the maintainer's own work, so
  moving them in is a relicensing the maintainer makes explicitly in the PR that moves them.
- Publishing: `eng/publish-device-lab.ps1` gains the portable single-file target. It replaces
  `eng/allyxlab-download.ps1` and `tools/AllyXLab/Downloads`.

## Delivery (stacked PRs, task branch `feat/devicelab-attended-wizard`)

1. Knowledge-base schema and matcher, the HC extractor, and records (curated: Claw 8 A2VM, ROG Ally
   X, Xbox Ally X; extracted: all HC classes), with tests. TFM change lands here.
2. Wizard shell, project model (save, reload, redo), self-elevation, preflight including PawnIO,
   identity, and export with privacy preview. `external/pawnio` pins, the `docs/decisions.md` entry
   and the AGENTS.md safety-boundary rewrite land here. AllyXLab's conflict and HidHide tests move
   with the code.
3. System dump: ACPI, SMBIOS, device tree, HID, sensors with all fields, EC dump, WMI, display,
   battery, CPU.
4. Buttons: init and restore, all-path listening, analog, chords, touchpad surface. AllyXLab's
   input-step and identity tests move here.
5. Motion (four sources) and rumble with the streaming worker message. AllyXLab's rumble tests move
   here.
6. Transports for fan, RGB, TDP and charge limit (known and unknown), `external/kx`, plus
   sleep/resume. AllyXLab's limit and fan-restore tests move here.
7. Scaffold from project and retire AllyXLab: delete the tool, its download, its test project and
   its verify step. Update `docs/device-projects.md`, `.agents/skills/wsgm-device-lab`, and
   `_plan/implementation-todo.md`; the Device Lab README and AGENTS.md are updated in every PR
   above.

Deviations recorded during PR 2 review (2026-09-24):

- The export preview is its own bounded record (`LabExportPreview`), because `CapturePrivacyPreview`
  is tied to the capture bundle model. It still uses the shared `CaptureRedactor`.
- AllyXLab's tests stay with AllyXLab until PR 7 deletes the tool; the ported code carries its own
  tests in `tests/WSGM.DeviceLab.Tests/Wizard`.
- Identity records CPUID family/model/stepping; the AMD SMU codename needs the PawnIO RyzenSMU
  module and is read in PR 6.
- The report is a `.wsgmlab` ZIP of the redacted project; reopening one is the PR 7 developer path.

Before the move is called done, list every AllyXLab feature (each `ActionKind`, input step, recovery
path, limit and export field) and name its new home, or say why it is gone.

## Verification

- **Build:** `dotnet build WSGM.slnx -c Release` warning-free, plus the portable single-file
  publish.
- **Manual testing:**
  - The maintainer runs the full wizard on the Claw 8 A2VM, which is the known path. Confirm that
    `MSI_Event` buttons, the legacy custom sensor fields and the MSI_ACPI TDP readback work.
  - The remote tester runs it on the Xbox Ally X: ATKACPI, the F17/F18/F21/F22 keys, whether the
    `0x5A` and FF31:0080 paths carry anything, and which rumble route works. Compare the result with
    the 0.2.1 run folder.
  - Reopen an exported project and redo one button. Check the privacy preview, and confirm there is
    no MSDM, serial or UUID in the ZIP.
- **PawnIO:**
  - On a machine without it, the wizard installs it silently.
  - If it's already installed, nothing is installed.
  - If an older version is installed, the tester is asked.
  - If the install fails, those tests are marked unavailable and the other stages still run.
- **Crash recovery:** kill the worker during a TDP write, a button init sequence and HidHide
  registration. After a restart, everything is restored.
- **Tests after manual testing:** focused `dotnet test tests\WSGM.DeviceLab.Tests` (knowledge-base
  matching and extractor output, ACPI filter, redaction, project round-trip, limits, axis
  derivation). Then run `eng\verify.ps1` once before the PR.
