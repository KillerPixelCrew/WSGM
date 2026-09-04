# OEM buttons and controller mapping

## Generic OEM discovery

1. Establish exact device and firmware identity before interpreting events.
2. Enumerate plausible passive sources: WMI event providers/activity, device-identified Raw Input,
   read-only HID input, DirectInput/SDL/XInput state, existing service/process events, and low-level
   keyboard observations.
3. Check whether Device Lab actually implements every needed source. Its current live capture
   factory observes inventory only. Before a live run, add and review bounded
   `IPassiveCaptureSource` implementations under `Capture/` for the required lanes and register them
   by extending or replacing the `ClosedObserveOnlyCaptureSource` construction path in
   `ObserveOnlyCaptureWorkflow`. Require fixtures plus sanitization, cancellation, unavailable,
   overflow, and refusal tests. Include a registered operator-marker source when the trial requires
   in-capture markers. A recipe cannot create an observer or marker by itself.
4. Capture neutral time, then one short press, release, long press, and repeated press per physical
   control. Insert operator markers and isolate each action from every other input.
5. Correlate event edges across sources and repeat until the same source/code appears with the
   action and not with controls. Record missing releases, duplicate delivery, latched semantics, and
   timing variability.
6. Choose the device-identifying source as the action source. Treat global keyboard hooks as
   ambiguous, suppression-only evidence.
7. Publish one closed `OemControlDescriptor` set and events with stable deduplication IDs. If two
   sources report one press, deduplicate; do not invoke the action twice.

Never disable ACPI/keyboard devices, suppress a broad chord, or allocate/log/do WMI work inside a
hook callback. A suppressor recognizes only the measured malformed sequence, fails open on unknown
or well-formed input, cleans unmatched modifiers precisely, and resets on disable, lock, suspend,
desktop/session changes, handoff, and fault.

## Generic raw controller mapping

Map a report empirically, not from a neighboring product:

1. Identify the exact collection by VID/PID, usage page/usage, report lengths, descriptor hash, and
   physical PnP association. Never bind by interface number or product string alone.
2. Save the original controller mode and physical location. Treat any vendor-mode switch as an
   attended mutation: require explicit operator direction, capture the original mode, establish a
   known rollback, recollect identity after switching, and verify restoration. Device Lab has no
   generic discovery-time mode-switch command. A switch can change PID and child paths; continue
   through stable parent `LocationPaths`, not null container IDs or a serial that exists in only one
   mode.
3. Capture many neutral reports. Establish report ID/length, constant prefix, axis centers/noise,
   hat neutral, and any corrupt first report.
4. Change one control at a time: press/release buttons, all D-pad diagonals, full/partial triggers,
   axis extrema and signs, stick clicks, touch/paddles, and OEM buttons.
5. Repeat simultaneous presses to test rollover and lost reports. Measure cadence, sequence
   behavior, disconnect/reconnect, suspend/resume, and mode-switch invalidation.
6. Encode each report as a complete `CanonicalControllerSample`: sticks `-1..1`, triggers `0..1`,
   finite measured motion, current cycle generation and increasing sequence. Do not synthesize an
   absent control.
7. Test the codec from raw byte fixtures before any target or HidHide path. Then perform one
   attended acquisition, release, or required mode-change trial and verify original-mode restoration
   by physical location.

The plugin reads physical hardware. WSGM alone creates the virtual target and edits its own HidHide
ledger. Unhiding before the plugin stops reading creates duplicate input.

For a paddle or OEM control that exists only in the plugin-selected controller mode, publish
`OemControlDescriptor.RequiresControllerAcquisition = true`. WSGM then refuses the action while
controller management is off. This does not weaken the plugin's obligation to restore the full
original mode and topology on release. Test both managed and unmanaged states plus restoration.

## MSI Claw 8 A2VM measured evidence

These facts apply only to manufacturer `Micro-Star International Co., Ltd.`, baseboard `MS-1T52`,
SKU `1T52.1`, supported firmware, and the observed endpoints. Revalidate a new revision.

Identity and topology:

- USB VID `0DB0`; PID `1901` is XInput and `1902` is DirectInput; `1903`/`1904` remain diagnostic.
- Controller/MCU release `0229` is the current exact gate.
- XInput MCU: `MI_01`, usage `FFA0/0001`, 64-byte input/output.
- DirectInput gamepad: `MI_00&COL01`, 64-byte input, 32-byte output, 48-byte feature.
- DirectInput MCU: `MI_00&COL02`, usage `FFF0/0040`, 64-byte input/output/feature.
- Mode continuation walks PnP parents to the physical USB location and removes unstable `#USBMI(n)`
  suffixes. The container id is null and the USB serial exists only in XInput.

Measured DirectInput report:

```text
neutral: 01 80 80 80 80 0F 00 00 00 00 ...
```

| Bytes/bits      | Meaning                                                    |
| --------------- | ---------------------------------------------------------- |
| 1-4             | left X/Y, right X/Y; center `0x80`; canonical Y is negated |
| 5 low nibble    | 8-way hat: 0 up, 2 right, 4 down, 6 left, F neutral        |
| 5 bits 4-7      | X, A, B, Y                                                 |
| 6 bits 0/1, 4-7 | LB, RB, View, Menu, L3, R3                                 |
| 7 bit 4         | left M1, DirectInput index 16, `RearPaddle1`               |
| 7 bit 3         | right M2, DirectInput index 15, `RearPaddle2`              |
| 8/9             | analog LT/RT                                               |

Handheld Companion's M1/M2 assignment is reversed for this measured device. Do not copy it. Drop the
corrupt initial state where bytes 1-9 are all `0xFF`.

Front OEM controls are not in this report. `MSI_Event` low byte `0x29` is OEM1 short/Guide; `0x58`
is OEM2 short/Quick Access; `0x2A` is OEM2 long/the same logical Quick Access control. Events have
no release, so the plugin uses independent 120 ms latches. A later event for one button must not
extend the other and fabricate a chord.

OEM2 also emits a malformed keyboard side effect through `ACPI\MSNB1001`: short is Win-down, orphan
G-up, Win-up; long substitutes orphan Tab-up. WMI is the action source. The hook suppresses only the
observed orphan-up sequence while Win is held and no modifier is active; it never blocks all
Win+G/Win+Tab or the full ACPI device.

Primary evidence and implementation paths:

- `_plan/claw-8-a2vm-plugin.md` — dated measurements and remaining attended matrix.
- `external/WSGM.Device.Msi.Claw8A2Vm/src/WSGM.Device.Msi.Claw8A2Vm/ClawInput.cs` — codec.
- `WindowsHidTransports.cs` — endpoint discovery, mode continuation, read/write behavior.
- `MsiWmiPlatform.cs` and `ClawResources.cs` — WMI event source and latches/suppression.
- Corresponding Claw tests — raw fixtures, mode, OEM, cleanup, and regression evidence.
