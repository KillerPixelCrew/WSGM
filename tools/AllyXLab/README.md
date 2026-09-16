# ROG Ally X Lab

A standalone, attended Windows x64 tester for the ASUS ROG Ally X (RC72LA) and ROG Xbox Ally X
(RC73XA). It listens on every input channel Windows offers at once and records all of them. Send
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

1. Open the EXE and press **Start**. Close games, disconnect other controllers and keep the battery
   above 30%. The first screen looks for Armoury Crate, Handheld Companion, G-Helper, MSI Center,
   Winhanced and similar managers, says what each does to the evidence, and offers to close them for
   you; services are listed but never stopped. If HidHide is active and this tool is not on its
   allowed list, it asks whether to add itself for the session and removes its own entry again at
   the end, leaving any other entry alone. In HidHide's inverse mode the list denies instead, so the
   tool changes nothing and tells you when it is listed.
2. Follow the single screen. In the input part there is nothing to confirm: it names one control,
   you press it, and the next one appears by itself. Each control is asked for once. If a control
   does nothing, press **Nothing happened**; **Do it again** repeats a step and **Skip the rest**
   leaves the section. Motion poses run on a countdown, and the rotation steps start when you move
   the device and end when it is still.
3. Rumble first tries each way of reaching the motors with one short buzz and asks whether you felt
   it; calibration then runs on the first one that worked. Hold the device and answer **Felt it** or
   **Didn't feel it** after each pulse and after the motors are silent. The wizard selects both
   motors, all phases, levels and pulse lengths. **Felt it** advances to a weaker/shorter pulse;
   **Didn't feel it** records the boundary. **Replay** repeats a pulse at your request. A/B also
   answer when exactly one XInput controller is connected; touch buttons always work. You never need
   to configure a sweep or select an interface.
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
- Which managers were running and what was closed at your request, whether HidHide and ViGEmBus are
  installed, and HidHide's active and inverse state with its allowed-application count.
- HID interface usages, report sizes and device revision for every collection. Paths are hashed.
- Raw Input from every HID, keyboard and mouse device, physical or virtual, including the vendor
  pages present on the machine. Reports are deduplicated and their changed bytes recorded; they are
  not treated as an already proven button map. Input with no device handle is marked as injected.
- Low-level keyboard and mouse hooks, which see injected input and its integrity level. Nothing is
  suppressed or modified. Key codes only, and only while the wizard is asking for a press.
- XInput slots 0-3 including the guide button, with the extended capabilities when the ordinal is
  present. Slots are unattributed observations; an XInput slot alone does not prove identity.
- Windows.Gaming.Input raw controllers and gamepads, where devices that only exist to the newer
  input stack appear.
- Firmware and ACPI events published through WMI, shell app commands (where volume keys surface
  without focus), power-setting notifications and device arrival and removal.
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

The read-only inventory can run on another Windows PC. Every other workflow requires a reviewed
model and board, a nonempty BIOS and the exact ASUS vendor endpoint. The original Ally X needs the
RC72LA model with an RC72L or RC72LA board and a nonempty SKU. The Xbox Ally X needs the RC73XA
model and board; its firmware reports no system SKU (RC73XA.317 inventory), so it has no SKU
requirement. The Xbox Ally RC73YA is not admitted, because its 20 W performance profile is below the
25 W power step. This bring-up family gate is not the production plugin's future firmware allowlist.

Ordinary steps run in a separate copy of the same executable with a 60-second supervisor deadline.
The guided rumble session uses one worker with the bounds described above. Hardware writes require
an explicit wizard action and a durably saved, acknowledged recovery checkpoint. The one change made
outside the device is HidHide's allowed-application list, only with your agreement: the previous
list is recorded, this tool's entry is added, and the list is written back when the session ends.
The hiding switch, the hidden-device list and every other HidHide setting stay untouched, and a
manager is only ever asked to close through its window. The worker reserves
`Global\WSGM.DeviceOwner`. Parent death requests cancellation; a blocked driver call can still
prevent cleanup. A timeout is reported as unknown restoration and blocks further writes until the
tester confirms recovery. A driver call completing is not independent readback.

Power and fan writes require two matching original snapshots, including both valid eight-point fan
curves. If any original state is unavailable, writes are refused. Applied and restored values are
read separately. The tool refuses restoration of an AC power envelope after a power-source change.
RGB and rumble have operator-confirmed baselines and explicit zero-output cleanup instead of a
fabricated readback. The RGB test uses HHD's output-report path only, without falling back to HC's
feature-report recipe. On the Xbox Ally X the tester is also asked to turn off Windows Dynamic
Lighting; the tool never writes the Dynamic Lighting interface itself.

The RC73XA inventory showed the FF31:0080 collection without an output report and no HID gamepad
collection. On that firmware the lighting and rumble steps are expected to report unavailable rather
than pick another interface.

No controller mode or button remapping is written: neither inspected reference establishes exact
readback/restoration of arbitrary original mappings. Capture native rear-button/chord behavior
first. No EC scanning, arbitrary WMI calls, raw command entry, firmware flashing, bypass charging,
controller hiding or virtual-controller installation is provided.

Rumble is driven through whichever route the device answers: HHD's output report on the gamepad
collection, HC's XInput vibration, or Windows.Gaming.Input. Each route is probed with one short
pulse and the tester's answer decides which one calibration uses; no route is assumed to work.

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
