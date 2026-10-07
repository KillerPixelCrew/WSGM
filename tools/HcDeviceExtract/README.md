# Handheld Companion knowledge extractor

Converts a supplied decompiled Handheld Companion tree into Device Lab knowledge records. The output
is reference evidence, not device drivers and not proof that a command is safe on hardware. This
development project is outside `WSGM.slnx`.

From the repository root, the maintained wrapper is:

```powershell
./eng/extract-hc-devices.ps1 -Reference _ref/HandheldCompanion -HcVersion 1.3.1.6
```

The wrapper expects `source/HandheldCompanion` and `Resources/Devices` under that reference
directory, runs the extractor and formats the emitted JSON. It rewrites `hc.*.json` under
`src/WSGM.DeviceLab/Knowledge/Devices`; other curated records are preserved. Review the output diff
and keep the input's provenance with the reference. The wrapper does not download or decompile HC.

The executable accepts explicit inputs and an existing output directory:

```text
HcDeviceExtract --source <source-dir> --resources <devices-dir> --output <knowledge-dir> --hc-version <version>
```

[`Program.cs`](Program.cs) contains the parser and extraction pipeline. Roslyn parses device
classes; `IdentityExtractor` follows HC's `IDevice.GetCurrent` switch, and constructor assignments,
OEM chords and loaded IMU JSON contribute to each record. `RecordBuilder` emits deterministically
ordered identities and preserves source/version evidence. `DefaultDevice` is excluded because it is
HC's fallback rather than a specific machine match. Existing `hc.*.json` files in the selected
output directory are deleted before records are rewritten.

Method bodies containing HID layouts, EC writes or initialization behavior are not converted into
executable plugin behavior. Overridden members are recorded for human curation. See the
[Device Lab README](../../src/WSGM.DeviceLab/README.md) and
[device authoring guide](../../docs/device-plugin-authoring.md) for using evidence to build a
package.
