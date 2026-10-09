# WSGM Device Lab

The authoring and diagnostic tool for LibHandheld source contributions and WSGM hardware research.
It inventories the handheld, captures observed behaviour, and creates exact identity source, decoder
research starters, recorded fixtures and provenance for a LibHandheld PR. Existing package
inspection and attended plugin diagnostics remain available for legacy evidence; new handhelds are
implemented in LibHandheld, not installed as device plugin DLLs.

It is a GUI and a CLI over the same code. The executable is `wsgm-device`.

## The tester wizard

Started with no arguments (or `wsgm-device wizard`), Device Lab opens a step-by-step test meant for
someone who is not a developer. It asks for administrator rights once, then:

1. **Get ready.** Holds WSGM's device-owner lock for the session, so WSGM's device integration
   cannot run beside the test. Lists other controller software that would hide or change the device
   and offers to close it (a close request only; services are never stopped). Asks before adding
   itself to HidHide's allowed programs, and removes exactly that entry when the window closes.
   Installs the pinned PawnIO driver when it is missing, and asks before replacing an older one.
2. **Your device.** Reads the board, BIOS, EC and processor identity, matches it against the known
   devices and asks the tester to confirm, or to type the product name and exact model.
3. **System details.** Read-only: every ACPI table (from the registry, so every SSDT is kept, and
   never MSDM or SLIC), SMBIOS with serials, UUIDs and asset tags removed, the device tree, every
   HID collection with its caps, serial ports, WMI classes and methods, WinRT and legacy sensors
   with every field, the AMD SMU identity or Intel power-limit registers, display, battery and power
   settings. It never reads the embedded controller's registers; the ACPI tables describe it.
4. **Buttons.** Runs a known device's controller init first (the Claw mode switch is switched back
   afterwards; the Ally button tables only on the tester's choice), or, for a device without a
   curated record, offers Handheld Companion's own mode commands one by one, each only if the tester
   chooses to try it (the motion stage does the same for gyro enables). It then learns what changes
   by itself, then asks for each control of the Xbox layout, the device's own buttons, back buttons,
   touchpads, stick touch, volume and power, holds and rear-button chords. Every input from every
   device is recorded (Raw Input on every usage page, direct reads of every accessible HID input
   collection, DirectInput game controllers, keyboard and mouse hooks, XInput with the Guide button,
   Windows.Gaming.Input, concrete WMI provider events and power events) and attributed afterwards.
   Unreadable collections are listed with their error. Windows-key shortcuts are swallowed while a
   step runs. A press step finishes by itself once the control is quiet; Previous button and the
   stage list can return to an earlier control or stage.
5. **Motion sensors.** WinRT, the legacy Sensor API (including custom fields such as the Claw's), a
   CH340 serial IMU and controller HID reports, all at once: rest, six gravity poses, and pitch,
   roll and yaw. The axis map is worked out per source and compared with the known record.
6. **Rumble.** Tries every route (XInput, Windows.Gaming.Input, and the device's own report where a
   curated record gives it), checks which motor is on which side, then live sliders to mark the
   weakest rumble felt and a pulse page for the shortest pulse. Motors are zeroed after every pulse
   and on every exit. The wizard sends probes and pulses as checkpointed worker calls and live
   slider levels over the worker's one-way stream. The worker zeroes a stream that goes quiet, the
   wizard asks it a few times a second whether a frame failed and shows the failure at once, and it
   collects the stream's write results when the slider stops. It records a route for startup cleanup
   before the first write; a recorded route that is no longer present is reported once and
   forgotten.
7. **Power, fans and lighting.** Telemetry on charger and on battery, with and without load, with
   fan RPM and temperatures from LibreHardwareMonitor on every device (read only; its controller and
   PSU groups stay off because their discovery writes to USB and serial devices). For a curated
   device it tests TDP, the power profile, fan curves and the charge limit through the device's own
   interface, each read back and put back. For other AMD and Intel machines it tests the processor
   power limit through PawnIO's RyzenSMU module or KX, and never writes the EC. Lighting uses
   Windows Dynamic Lighting, the Ally's Aura interface, and the Claw's committed RGB profile where
   they apply. The Claw test runs both fans at full speed for five seconds and restores the original
   mode flags. RGB testing restores the exact original profile, including after a cancelled test.
8. **Sleep and wake.** The tester presses the power button; the wizard never sleeps the device
   itself. It checks that the controller, HID devices and sensors come back, that a press arrives,
   and whether a controller init survived. Curated controller init commands also run in the hardware
   worker. On Modern Standby systems the display off/on cycle also advances the test; the evidence
   names that signal separately from traditional suspend/resume and does not claim deep-idle
   residency.
9. **Finish and share.** Shows every file that will be shared, what was replaced (account names,
   user folders, device instance paths, network addresses) and what stays on the computer, then
   writes one `.wsgmlab` file, a ZIP of the redacted test folder.

Each new test is a dated folder next to `wsgm-device.exe`. Selecting a stage in the list shows its
result; "Run again" starts a new attempt, and every attempt is kept, so a wrongly read button does
not mean repeating the whole test. "Stop and save" (or Escape) ends the running step and keeps what
was recorded. Result-page Continue advances only after the current operation has completed; the next
stage remains tracked, and closing waits for its cleanup. Power's Continue cannot overlap a
restoration attempt. Changes the wizard makes to the machine are recorded in
`%LOCALAPPDATA%\WSGM Device Lab\wizard` before they are made, so a session that was killed is put
back the next time the wizard starts.

Writes with readable originals run in the elevated hardware worker behind an acknowledged
checkpoint. Recorded mode commands without readable originals and read-only collection stay in the
wizard. A failed restore keeps its recovery record; streamed rumble keeps its safety zero armed
until the zero is written.

The wizard and its hardware worker also write `wsgm-device.log` beside `wsgm-device.exe` (or in the
temp folder when that folder cannot be written). Every line is on disk before the step it names
runs, except streamed frames, status polls and calls marked sampled; failures are still traced.
After a crash or a hard reset its last lines say which stage, dump section, worker call or PawnIO
function was running. It names steps only, never device paths, serials or user folders, and is not
part of the shared report: ask the tester to send it alongside. The file is only appended to, so
earlier sessions stay in it.

A returned report is read with the developer commands:

```powershell
wsgm-device report  test.wsgmlab                       # every step and its summary
wsgm-device review  test.wsgmlab                       # agreements and disagreements with the known record
wsgm-device promote test.wsgmlab --out new-record.json # a curated record with lab-confirmed facts
wsgm-device scaffold --from test.wsgmlab --out-dir my-contribution
```

Review uses the power summary's pass rules, including matched-write readback, and requires
successful restoration for confirmation. Readback mismatches remain Unresolved. Generic
processor-power observations over Ryzen SMU or KX stay Observed and have no default device-mechanism
promotion. Lighting's "matched" answers count as passes; MSI full-speed fan evidence uses the `fan`
feature. The rumble method description names the exact pulse lengths the wizard offers.

`wsgm-device gui` opens the developer tabs described below instead, including a "Lab report" tab for
the same review. For a remote tester, publish one self-contained file:

```powershell
.\eng\publish-device-lab.ps1 -Portable -OutputRoot publish/DeviceLabPortable
```

## Why it is a separate tool

Implementing a native handheld family means answering questions about a specific machine that no
documentation will tell you: which EC register the fan curve lives behind, what the OEM button
reports, whether a power-limit write actually took. Device Lab exists to answer those before you
implement the native engine, and to prove the answers afterwards.

It lives in the WSGM repository but ships separately. WSGM goes to end users and owns a live
session; Device Lab is a developer tool that runs offline, on a machine that may not have WSGM
installed at all.

## The workflow

```powershell
# 1. What is this machine?
wsgm-device doctor    --out-dir diagnostics
wsgm-device inventory --out-dir inventory --shareable
wsgm-device candidates --from inventory/inventory.json # which known device is this?

# 2. Capture it, then read what you captured.
wsgm-device capture    run --recipe recipe.json --out-dir captures # attended; you approve its scope
wsgm-device inspect    capture.wsgmcap
wsgm-device compare    before.wsgmcap after.wsgmcap
wsgm-device correlate  capture.wsgmcap --action <id> --sources <id,id>

# 3. Create a LibHandheld contribution with capture-backed fixtures.
wsgm-device scaffold --from capture.wsgmcap --out-dir my-contribution `
    --usb-instance <exact-instance-id> # required only with several exact endpoints

# 4. Implement and review native source in the LibHandheld checkout.
# Copy generated C# into src/LibHandheld/Families/<family>, implement the engine,
# add fixture-backed decoder tests and register only completed device support.
# Submit a LibHandheld PR with sanitized evidence; do not pack a device DLL.
```

`inventory --shareable` is the form meant for a bug report: it keeps the device facts and drops the
identifying ones.

## Known devices

`candidates` compares the inventory against the knowledge base compiled into Device Lab and lists
every record whose identity rules match, with the fields that matched. A tester confirms the match;
Device Lab never assumes it.

The compiled read probes (`probe-read`) belong to a curated record. They are offered only when that
record's exact identity rule, its controller USB IDs and its WMI provider all match the inventory,
and `candidates` explains each of those comparisons. The probes themselves, and the few facts the
record schema does not hold (the logical device ID, the reference controller release), live in
`Probes/ReadProbeProfiles.cs`.

The records live in `Knowledge/Devices` and come in two kinds:

- **Extracted** (`hc.*.json`) are generated from the decompiled Handheld Companion source by
  `eng/extract-hc-devices.ps1`. They hold what HC states declaratively: its device switch, power
  ranges, capability flags, OEM key chords, EC fan registers and the IMU axis map HC actually
  applies. Behaviour HC implements inside methods is listed as overridden members, not guessed.
  Nothing in an extracted record is hardware-verified, and HC's own mistakes are recorded as hazards
  rather than corrected.
- **Curated** (`wsgm.*.json`) are written by hand from a WSGM plugin, a lab run or reviewed HC
  source, with the evidence for each fact. Only a curated record carries button mappings, write
  mechanisms with readback, or can supersede an extracted record.

Regenerate the extracted records after updating the reference, and review the diff:

```powershell
.\eng\extract-hc-devices.ps1
```

## Attended versus unattended

The split is enforced, not advisory.

**Read-only or offline:** `validate`, `inspect`, `compare`, `correlate`, `inventory`, `doctor` and
`pack`. `validate` never loads plugin code. It checks the manifest, the package layout and that the
entry assembly is a managed x64 image, all statically.

**Unattended but running your code:** `test sample` and `test plugin` load and run plugin code in a
contained worker with your authority. Only `validate` is fully static.

**Attended:** `test hardware` writes to the device, so it demands an explicit action, a state
directory you named, and your presence. It exists because a capability write is only ever proven on
real hardware. Detection and the whole attended lifecycle run in an authenticated disposable worker
process. Device Lab kills the full process tree at the hard deadline and keeps the production owner
slot reserved if cleanup could not be verified.

## Capture exports and privacy

Before you confirm an export, Device Lab shows a bounded projection of the actual sanitized bundle:
the root documents in full, every stream, analysis and blob represented by exact counts and hashes,
and large content sampled rather than quietly left out of the privacy review.

Redaction uses one token map across the inventory, the recipe and the streams. If redaction would
merge two source identifiers into one token, the export stops with an error before it creates a
bundle or a preview.

Imported markers need valid UTF-8 and a named kind. Explicit restart and resume segments keep their
reason even when the receipt arrives late. Malformed read-probe data comes back as a rejection along
with any earlier samples, and a declared numeric bound requires an in-range numeric value, version
responses included.

A cancelled inventory write, or a failed or cancelled export, reports the leftover temporary path if
cleanup did not manage to remove it. When cleanup succeeds, a cancelled export still reports as
cancelled.

Output paths are checked before anything is written. A broad home directory, a repository root or an
existing reparse point is refused rather than written into.

## LibHandheld contribution output

`scaffold` emits source and research data without a project, package manifest or WSGM assembly
reference. `DeviceIdentity.cs` uses LibHandheld's public identity contracts. `ReportDecoder.cs`
rejects reports until verified decoding is implemented. A capture includes deterministic `fixtures/`
with its exact retained-input SHA-256; a lab report includes `DeviceProfile.cs` with confirmed
buttons, axis maps, roles and provenance. `contribution.json` explicitly marks the contribution
unimplemented. Read the generated README before integrating it in a family.

The included MIT license has a contributor placeholder. Complete it and follow LibHandheld's
contribution rules. Device Lab's GPL license does not change the generated contribution license.

## Building

Run from the WSGM repository root. Device Lab's helper contracts now come from the single
`src/WSGM.Plugin.Sdk` assembly; existing diagnostic namespaces are retained.

```powershell
dotnet build src/WSGM.DeviceLab/WSGM.DeviceLab.csproj
dotnet test tests/WSGM.DeviceLab.Tests/WSGM.DeviceLab.Tests.csproj
```

## Licence

GPL-3.0-or-later, the same licence as WSGM, see the repository's `LICENSE`. Device Lab compiles
WSGM's own interop sources from `src/Shared/Interop` (`Kernel32.cs`, `NativeHidHide.cs` and
`HidHideControl.cs`). Third-party components it redistributes keep their own licences, see
`THIRD_PARTY_NOTICES.md`.

Parts of the wizard are ported from the AllyXLab tool.

Capability publications can include SDK prominence and companion hints. Device Lab checks them with
`CapabilityLayout.TryValidate` before an attended capability action; these hints describe host
presentation and never permit another hardware write.
