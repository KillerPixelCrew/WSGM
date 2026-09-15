# ROG Ally X Lab

A standalone, attended Windows x64 tester for the original ASUS ROG Ally X RC72LA. Send
`AllyXLab.exe` to the tester. It includes .NET and needs no WSGM installation, Python, PowerShell
script, or separate plugin. Windows requests administrator access when it starts.

This is an experimental bring-up tool. Source inspection and compilation do not establish that its
hardware commands work on a particular BIOS. A refused getter or identity check is a useful result;
export it rather than bypassing the refusal. The production Ally X plugin remains passive.

## Download

[Download AllyXLab.exe](https://github.com/KillerPixelCrew/WSGM/raw/refs/heads/master/tools/AllyXLab/Downloads/AllyXLab.exe)

The binary is committed beside this source at the maintainer's request. `Downloads/SHA256.txt`
records its hash. It is an unsigned experimental build; no hardware pass is claimed.

## Tester instructions

1. Disconnect other gamepads. Close games and device managers, including WSGM, Handheld Companion,
   G-Helper and Armoury Crate. The tool refuses hardware writes while known competing applications
   are running and never stops them or installs drivers. Some Armoury Crate background processes may
   need to be closed by the tester; the ASUS driver must remain installed.
2. Keep the battery above 30%, keep the vents clear, and do not change AC/DC state during a test.
   Start with **Collect identity and interfaces**, then **Read power / fan state**.
3. In **Buttons / motion**, select one named step at a time. Press **Start selected capture**, then
   wait for **CAPTURING NOW** before moving. Capture neutral first, individual buttons, holds,
   sticks/triggers, diagonals and chords. Note any launched OEM app or missing input.
4. Capture the stationary gyro step without touching the device. Repeat after warming up. Capture
   the six gravity poses and the three approximately 90-degree rotations. The output contains raw
   fields, timestamps and per-field mean, standard deviation and range. These establish bias, axis
   and scale candidates; the tool does not write sensor calibration or assume unknown units.
5. In **Power / fans**, read first, then test one TDP envelope or firmware profile. Suggested TDP
   values are 13 W and 17 W, with 25 W optional. The envelope sets all three limits to that value.
   Firmware profile tests record the actual resulting limits rather than assuming preset values.
   Repeat on AC and DC as separate tests. The fan test raises one captured curve by 15 percentage
   points, capped at 99, and never lowers a captured duty value. Record which fan changed.
6. In **Rumble calibration**, select the inventoried endpoint, one motor and one phase. Fire a step,
   confirm the motors stopped, then record **Felt** or **Did not feel**. Complete sustained
   strength, 30 ms tick strength and pulse-duration phases for both motors. A floor is a human
   observation, not hardware readback. Add a note identifying the physical motor location.
7. In **RGB**, establish an OFF baseline using OEM controls and close the other manager. Test the
   primary colors and individual ring zones. Confirm the lights are OFF after each test and record
   the actual illuminated zone. The remembered RGB mode/color may change; restore those using OEM
   controls afterward. No automatic prior-color readback or exact RGB restoration is claimed.
8. Use **STOP / restore** to cancel. Wait for restoration before closing. If cleanup is unknown,
   review the recovery record and restore the recorded settings with OEM controls before confirming
   recovery. Do not retry an uncertain write.
9. Use **Open capture folder** to review the JSON, then **Review / export ZIP**. Send the ZIP back.
   Nothing is uploaded automatically.

## What is captured

- Model, board, SKU, BIOS, EC version, OS version, tool version/hash and reference revisions.
- ASUS `0B05:1B4C` HID interface usages, report sizes and device revision. Paths are hashed.
- ASUS-only Raw Input HID and keyboard events. Other keyboards are discarded. Raw reports and
  changed-byte candidates are retained; they are not treated as an already proven button map.
- A restricted, non-suppressing keyboard observer for volume/mute and F17/F18 only, plus Windows
  power broadcasts. These are secondary, unattributed signals; no other keyboard keys are retained.
- XInput slots as separate, unattributed observations. Disconnect other controllers to reduce
  ambiguity; an XInput slot alone does not prove physical identity.
- Windows legacy Sensor API metadata and numeric motion fields, timestamp deduplication and
  stationary statistics. Unsupported or blocked sensors are reported without requesting permission
  or changing their report intervals.
- Raw ASUS ACPI responses, supported scalar values, profile-specific fan curves, before/after
  snapshots, four readback samples and restoration readback.
- Rumble output, measured software pulse duration, RGB output and tester feedback.

The tool does not capture arbitrary keyboard text, machine/user names, serial numbers or raw PnP
paths intentionally. Raw device payloads and free-text notes still require review before sharing.
Each session is saved under `%LOCALAPPDATA%\WSGM.AllyXLab`. A recovery checkpoint survives
incomplete worker runs in `recovery-required.json` beside the session directories.

## Boundaries

The read-only inventory can run on another Windows PC. Every other workflow requires the reviewed
ASUS RC72LA model/board family, a nonempty SKU and BIOS, and the exact ASUS vendor endpoint. This
bring-up family gate is not the production plugin's future firmware allowlist.

Every step runs in a separate copy of the same executable with a 60-second supervisor deadline.
Hardware writes require a GUI confirmation and a durably saved, acknowledged recovery checkpoint.
The worker reserves `Global\WSGM.DeviceOwner`. Parent death requests cancellation; a blocked driver
call can still prevent cleanup. A timeout is reported as unknown restoration and blocks further
writes until the tester confirms recovery. A driver call completing is not independent readback.

Power and fan writes require two matching original snapshots, including both valid eight-point fan
curves. If any original state is unavailable, writes are refused. Applied and restored values are
read separately. The tool refuses restoration of an AC power envelope after a power-source change.
RGB and rumble have operator-confirmed baselines and explicit zero-output cleanup instead of a
fabricated readback. The RGB test uses HHD's output-report path only, without falling back to HC's
feature-report recipe.

No controller mode or button remapping is written: neither inspected reference establishes exact
readback/restoration of arbitrary original mappings. Capture native rear-button/chord behavior
first. No EC scanning, arbitrary WMI calls, raw command entry, firmware flashing, bypass charging,
controller hiding or virtual-controller installation is provided.

The rumble sequence follows Device Lab's Claw calibration in
`src/WSGM.DeviceLab/Testing/AttendedPluginAction.cs`. Unlike the Claw's full-scale continuous sweep,
this first Ally X version caps drive at 50% and bounds sustained steps to 1.5 seconds. Reported
boundaries must retain that method. Sensor COM definitions come from the repository's
`tools/probe-legacy-sensors.ps1`. HHD is primary and HC supplies Windows transport cross-checks; see
[the pinned source comparison](../../src/WSGM.Device.Asus.RogAllyX/REFERENCE.md).

## Build

```powershell
dotnet publish tools/AllyXLab/WSGM.AllyXLab.csproj --configuration Release --output publish/ally-x-lab
```

Only `AllyXLab.exe` is needed by the tester. This tool is separate from the production solution and
installer, like the other developer tools under `tools`. It contains no HC implementation or assets.
It follows WSGM's GPL-3.0-or-later license; the Device SDK/plugin's MIT license does not apply to
this tool. The Windows desktop runtime and System.Management retain their own licenses.

Compilation and formatting may run before manual testing. Automated test suites and the full
repository gate follow the root manual-testing-first policy. Live execution is reserved for the
attended tester; no live pass is implied by the distributable.

Hardware-free guard tests live in `tests/WSGM.AllyXLab.Tests`. Build them without executing suites
before the tester's manual run; run the focused project afterward. The portable tester is an
explicitly requested developer tool, not a second generic mutation command in Device Lab.
