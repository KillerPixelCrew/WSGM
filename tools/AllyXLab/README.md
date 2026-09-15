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

1. Open the EXE and press **Start**. Close games, WSGM, HC, G-Helper and Armoury Crate first;
   disconnect other controllers and keep the battery above 30%.
2. Follow the single screen. It chooses the sequence and settings. Read the instruction, press
   **Ready**, and perform the named button press or movement when **CAPTURING NOW** appears. The
   next instruction appears automatically after the capture. Sensor streams are recorded during
   motion steps rather than repeated during every button test. **Skip** records an unavailable step.
3. During rumble, hold the device and answer **Felt it** or **Didn't feel it** after each pulse and
   after the motors are silent. The wizard selects both motors, all phases, levels and pulse
   lengths. **Felt it** advances to a weaker/shorter pulse; **Didn't feel it** records the boundary.
   **Replay** repeats a pulse at your request. A/B also answer when exactly one XInput controller is
   connected; touch buttons always work. You never need to configure a sweep or select an interface.
4. Follow the lighting, charger and power/fan instructions. The wizard chooses colors, zones,
   profiles and limits. Confirm what you see/hear. Power/fan tests read and restore original state.
   RGB starts from a tester-confirmed OFF baseline; restore the remembered color/mode in the OEM
   controls afterward.
5. **Stop / save progress** or Escape cancels and waits for cleanup. Skipped, failed and interrupted
   steps remain marked; they are never counted as completed measurements.
6. At the end, review **Capture files** if desired and press **Save ZIP to Desktop**. Send that ZIP
   back. The same finish screen is available after stopping early. No upload occurs automatically.

Technical details and license notices remain available through the footer. Unknown or ambiguous
interfaces are recorded as unavailable instead of asking the tester to guess.

### Rumble calibration

One worker holds the selected device for the whole calibration; it does not reopen and re-inventory
between pulses. The six phases cover both motors independently:

- Sustained strength: 1.5-second bursts at 50, 32, 20, 12, 8, 5, 3 and 1 percent.
- Short-tick strength: the same levels, with 30 ms pulses.
- Pulse duration: 200, 120, 70, 40, 25, 15, 10 and 5 ms at 50 percent.

Each phase starts on **Ready** and ends at the first not-felt response or an explicit answer to the
last pulse. It records the lowest confirmed felt value, the first not-felt value, and whether the
boundary is bracketed, below the tested range, or absent because nothing was felt at the ceiling. A
phase that is stopped stays incomplete. The report contains every actual pulse and answer plus
per-motor boundaries. No default threshold is substituted for a missing observation.

All pulses have zero-output cleanup before the feedback question. Replays are explicit, limited to
two per trial, and do not advance the score. The interactive worker has a five-minute budget plus a
30-second supervisor cleanup allowance. The ordinary test workers keep their 60-second deadline.

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

Ordinary steps run in a separate copy of the same executable with a 60-second supervisor deadline.
The guided rumble session uses one worker with the bounds described above. Hardware writes require
an explicit wizard action and a durably saved, acknowledged recovery checkpoint. The worker reserves
`Global\WSGM.DeviceOwner`. Parent death requests cancellation; a blocked driver call can still
prevent cleanup. A timeout is reported as unknown restoration and blocks further writes until the
tester confirms recovery. A driver call completing is not independent readback.

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
this Ally X version caps drive at 50% and bounds sustained steps to 1.5 seconds. Reported boundaries
must retain that method. Sensor COM definitions come from the repository's
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
